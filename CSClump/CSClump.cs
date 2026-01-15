using CSUtils;
using ScriptPortal.Vegas;
using System.Collections.Generic;
using System.Linq;

namespace VegasScripting
{
	public class EntryPoint
	{
		private class TrackEventInfo
		{
			public TrackEvent Event { get; set; }
			public Timecode OriginalStart { get; set; }
			public Timecode OriginalEnd { get; set; }
			public Timecode NewStart { get; set; }
			public Marker TransitionMarker { get; set; }
			public Timecode FadeLength { get; set; }
		}

		private class MarkerInfo
		{
			public Marker Marker { get; set; }
			public string Label { get; set; }
			public Timecode OriginalPosition { get; set; }
			public Timecode NewPosition { get; set; }
		}

		public void FromVegas(Vegas vegas)
		{
			VideoTrack targetTrack = TrackSelection.GetTargetTrack(vegas);
			CollapseTracksOptimized(targetTrack, vegas.Project);

			// Debug output
			System.Text.StringBuilder debug = new System.Text.StringBuilder();
			debug.AppendLine("Final clip positions:");
			foreach (TrackEvent te in targetTrack.Events.OrderBy(e => e.Start))
			{
				debug.AppendLine($"Clip {te.Index}: Start={te.Start.ToMilliseconds()}ms, End={te.End.ToMilliseconds()}ms, Length={te.Length.ToMilliseconds()}ms");
			}
			debug.AppendLine("\nTransition markers:");
			foreach (Marker m in vegas.Project.Markers.Where(m => m.Label == "v").OrderBy(m => m.Position))
			{
				debug.AppendLine($"Marker 'v' at {m.Position.ToMilliseconds()}ms");
			}
			//System.Windows.Forms.MessageBox.Show(debug.ToString(), "Debug Info");
		}

		private void CollapseTracksOptimized(VideoTrack mainTrack, Project proj)
		{
			List<TrackEventInfo> trackInfos = new List<TrackEventInfo>();
			List<MarkerInfo> markerInfos = new List<MarkerInfo>();

			// Phase 1: Calculate all new positions for main track
			Timecode currentPosition = new Timecode(0);
			System.Text.StringBuilder calcDebug = new System.Text.StringBuilder();
			calcDebug.AppendLine("Phase 1 - Calculating positions:");

			foreach (TrackEvent trackEvent in mainTrack.Events.OrderBy(te => te.Start))
			{
				TrackEventInfo info = new TrackEventInfo
				{
					Event = trackEvent,
					OriginalStart = trackEvent.Start,
					OriginalEnd = trackEvent.End
				};

				if (trackEvent.Index == 0)
				{
					info.NewStart = new Timecode(0);
					currentPosition = trackEvent.Length;
					calcDebug.AppendLine($"Clip {trackEvent.Index}: NewStart=0ms, Length={trackEvent.Length.ToMilliseconds()}ms");
				}
				else
				{
					TrackEventInfo previousInfo = trackInfos[trackEvent.Index - 1];

					// Find transition marker in ORIGINAL positions before any moves
					// Look for markers within the previous clip's range
					Marker transitionMarker = FindOriginalTransitionMarker(proj, previousInfo.OriginalStart, previousInfo.OriginalEnd);

					if (transitionMarker != null)
					{
						// Calculate fade length from original positions
						info.FadeLength = previousInfo.OriginalEnd - transitionMarker.Position;
						info.TransitionMarker = transitionMarker;

						// Clip starts before the end of previous clip (overlap by fade length)
						// currentPosition is where the previous clip ends in the NEW timeline
						Timecode previousNewEnd = previousInfo.NewStart + previousInfo.Event.Length;
						info.NewStart = previousNewEnd - info.FadeLength;

						calcDebug.AppendLine($"Clip {trackEvent.Index}: Transition found!");
						calcDebug.AppendLine($"  TransitionMarker at {transitionMarker.Position.ToMilliseconds()}ms");
						calcDebug.AppendLine($"  PreviousOriginalEnd={previousInfo.OriginalEnd.ToMilliseconds()}ms");
						calcDebug.AppendLine($"  PreviousNewEnd={previousNewEnd.ToMilliseconds()}ms");
						calcDebug.AppendLine($"  FadeLength={info.FadeLength.ToMilliseconds()}ms");
						calcDebug.AppendLine($"  NewStart={info.NewStart.ToMilliseconds()}ms (should overlap)");

						// Current position advances to the end of this clip
						currentPosition = info.NewStart + trackEvent.Length;
					}
					else
					{
						// No transition, clip starts at end of previous
						info.NewStart = currentPosition;
						currentPosition = info.NewStart + trackEvent.Length;
						calcDebug.AppendLine($"Clip {trackEvent.Index}: No transition, NewStart={info.NewStart.ToMilliseconds()}ms");
						calcDebug.AppendLine($"  Previous clip range: {previousInfo.OriginalStart.ToMilliseconds()}ms to {previousInfo.OriginalEnd.ToMilliseconds()}ms");
					}
				}

				trackInfos.Add(info);

				// Calculate marker repositions for this track event
				CalculateMarkerRepositions(proj, info, markerInfos);
			}

			
			//System.Windows.Forms.MessageBox.Show(calcDebug.ToString(), "Calculation Debug");

			// Phase 1.5: Calculate shifts for clips on other tracks
			List<TrackEventInfo> otherTrackInfos = CalculateOtherTrackShifts(proj, mainTrack, trackInfos);

			// Phase 2: Apply all changes

			// Apply marker changes (remove and recreate to handle position changes)
			foreach (MarkerInfo markerInfo in markerInfos.OrderByDescending(m => m.OriginalPosition))
			{
				if (markerInfo.NewPosition != markerInfo.OriginalPosition)
				{
					proj.Markers.Remove(markerInfo.Marker);
					proj.Markers.Add(new Marker(markerInfo.NewPosition, markerInfo.Label));
				}
			}

			// Apply track changes in FORWARD order (so earlier clips are in place before later ones)
			foreach (TrackEventInfo info in trackInfos.OrderBy(i => i.Event.Index))
			{
				info.Event.AdjustStartLength(info.NewStart, info.Event.Length, false);

				if (info.TransitionMarker != null && info.Event.Index > 0)
				{
					TrackEventInfo previousInfo = trackInfos[info.Event.Index - 1];
					previousInfo.Event.FadeOut.Length = info.FadeLength;
					info.Event.FadeIn.Length = info.FadeLength;
				}
			}

			// Apply other track changes
			foreach (TrackEventInfo info in otherTrackInfos)
			{
				info.Event.AdjustStartLength(info.NewStart, info.Event.Length, false);
			}
		}

		private List<TrackEventInfo> CalculateOtherTrackShifts(Project project, Track mainTrack, List<TrackEventInfo> mainTrackInfos)
		{
			List<TrackEventInfo> otherTrackInfos = new List<TrackEventInfo>();

			// Get all tracks except "main" and "music"
			List<Track> otherTracks = new List<Track>();
			foreach (Track t in project.Tracks)
			{
				if (t != mainTrack &&
					(t.Name == null || (t.Name.ToLower() != "main" && t.Name.ToLower() != "music")))
				{
					otherTracks.Add(t);
				}
			}

			foreach (Track track in otherTracks)
			{
				foreach (TrackEvent te in track.Events)
				{
					// Find which main track clip this clip's START overlaps with
					// This ensures clips move with the main clip they start during
					TrackEventInfo overlappingMainClip = null;
					foreach (TrackEventInfo info in mainTrackInfos)
					{
						if (te.Start >= info.OriginalStart && te.Start < info.OriginalEnd)
						{
							overlappingMainClip = info;
							break;
						}
					}

					if (overlappingMainClip != null)
					{
						// Calculate the shift for this main clip
						Timecode shift = overlappingMainClip.NewStart - overlappingMainClip.OriginalStart;

						// Apply the same shift to this clip
						otherTrackInfos.Add(new TrackEventInfo
						{
							Event = te,
							OriginalStart = te.Start,
							OriginalEnd = te.End,
							NewStart = te.Start + shift
						});
					}
				}
			}

			return otherTrackInfos;
		}

		private Marker FindOriginalTransitionMarker(Project proj, Timecode previousEventStart, Timecode previousEventEnd)
		{
			// Find transition markers (labeled "v") within the previous event's range
			return proj.Markers
				.Where(m => m.Label == "v")
				.Where(m => m.Position > previousEventStart && m.Position <= previousEventEnd)
				.OrderByDescending(m => m.Position) // Get the last one (closest to the end)
				.FirstOrDefault();
		}

		private void CalculateMarkerRepositions(Project proj, TrackEventInfo trackInfo, List<MarkerInfo> markerInfos)
		{
			Timecode shift = trackInfo.NewStart - trackInfo.OriginalStart;

			if (shift.ToMilliseconds() == 0)
				return;

			// Find markers that need to move with this track event
			// Markers move if they're within the track's original range
			// BUT exclude transition markers at the END (they belong to the next clip's calculation)
			IEnumerable<Marker> affectedMarkers = proj.Markers.Where(m =>
				m.Position > trackInfo.OriginalStart && // Exclude markers exactly at start
				m.Position < trackInfo.OriginalEnd && // Exclude markers at or after end
				!markerInfos.Any(mi => mi.Marker == m) // Not already processed
			);

			foreach (Marker marker in affectedMarkers)
			{
				markerInfos.Add(new MarkerInfo
				{
					Marker = marker,
					Label = marker.Label,
					OriginalPosition = marker.Position,
					NewPosition = marker.Position + shift
				});
			}

			// Handle transition marker at the END of this track (if exists)
			// This marker needs special handling - it moves to maintain position relative to this track's new end
			if (trackInfo.TransitionMarker == null)
			{
				Marker transitionAtEnd = proj.Markers
					.Where(m => m.Label == "v")
					.Where(m => m.Position >= trackInfo.OriginalEnd - new Timecode(10) && m.Position <= trackInfo.OriginalEnd)
					.OrderByDescending(m => m.Position)
					.FirstOrDefault();

				if (transitionAtEnd != null && !markerInfos.Any(mi => mi.Marker == transitionAtEnd))
				{
					// Calculate new position: maintain the same offset from the end
					Timecode offsetFromEnd = trackInfo.OriginalEnd - transitionAtEnd.Position;
					Timecode newTrackEnd = trackInfo.NewStart + trackInfo.Event.Length;

					markerInfos.Add(new MarkerInfo
					{
						Marker = transitionAtEnd,
						Label = transitionAtEnd.Label,
						OriginalPosition = transitionAtEnd.Position,
						NewPosition = newTrackEnd - offsetFromEnd
					});
				}
			}
		}
	}
}