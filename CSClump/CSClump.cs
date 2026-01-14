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
			CollapseTracksOptimized(targetTrack);

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
			System.Windows.Forms.MessageBox.Show(debug.ToString(), "Debug Info");
		}

		private void CollapseTracksOptimized(VideoTrack mainTrack)
		{
			Project proj = mainTrack.Project;
			List<TrackEventInfo> trackInfos = new List<TrackEventInfo>();
			List<MarkerInfo> markerInfos = new List<MarkerInfo>();

			// Phase 1: Calculate all new positions
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
					Marker transitionMarker = FindOriginalTransitionMarker(proj, previousInfo.OriginalEnd);

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
					}
				}

				trackInfos.Add(info);

				// Calculate marker repositions for this track event
				CalculateMarkerRepositions(proj, info, markerInfos);
			}

			System.Windows.Forms.MessageBox.Show(calcDebug.ToString(), "Calculation Debug");

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
		}

		private Marker FindOriginalTransitionMarker(Project proj, Timecode previousEventEnd)
		{
			// Find transition markers (labeled "v") at or before the previous event end
			// Only consider markers that are actually at the event boundary
			var candidates = proj.Markers
				.Where(m => m.Label == "v")
				.Where(m => m.Position >= previousEventEnd - new Timecode(10) && m.Position <= previousEventEnd)
				.OrderByDescending(m => m.Position)
				.ToList();

			// Debug
			System.Text.StringBuilder markerDebug = new System.Text.StringBuilder();
			markerDebug.AppendLine($"Looking for transition marker near {previousEventEnd.ToMilliseconds()}ms");
			markerDebug.AppendLine($"Search range: {(previousEventEnd - new Timecode(10)).ToMilliseconds()}ms to {previousEventEnd.ToMilliseconds()}ms");
			markerDebug.AppendLine($"Found {candidates.Count} candidates");
			foreach (var c in candidates)
			{
				markerDebug.AppendLine($"  Candidate at {c.Position.ToMilliseconds()}ms");
			}
			if (candidates.Count > 0)
			{
				System.Windows.Forms.MessageBox.Show(markerDebug.ToString(), "Transition Marker Search");
			}

			return candidates.FirstOrDefault();
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