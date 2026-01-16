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
			public Timecode FadeLength { get; set; }
			public bool HasTransition { get; set; }
		}

		public void FromVegas(Vegas vegas)
		{
			VideoTrack targetTrack = TrackSelection.GetTargetTrack(vegas);
			CollapseTracksOptimized(targetTrack, vegas.Project);
		}

		private void CollapseTracksOptimized(VideoTrack mainTrack, Project proj)
		{
			List<TrackEventInfo> trackInfos = new List<TrackEventInfo>();

			// Phase 1: Calculate all new positions for main track
			Timecode currentPosition = new Timecode(0);

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
				}
				else
				{
					TrackEventInfo previousInfo = trackInfos[trackEvent.Index - 1];

					// Check if there's a crossfade between previous and current clip
					// Both clips need fade in/out set and they should roughly match
					Timecode prevFadeOut = previousInfo.Event.FadeOut.Length;
					Timecode currFadeIn = trackEvent.FadeIn.Length;

					if (prevFadeOut.ToMilliseconds() > 0 && currFadeIn.ToMilliseconds() > 0)
					{
						// Use the average of the two fade lengths
						info.FadeLength = new Timecode((prevFadeOut.ToMilliseconds() + currFadeIn.ToMilliseconds()) / 2.0);
						info.HasTransition = true;

						// Clip starts before the end of previous clip (overlap by fade length)
						Timecode previousNewEnd = previousInfo.NewStart + previousInfo.Event.Length;
						info.NewStart = previousNewEnd - info.FadeLength;

						currentPosition = info.NewStart + trackEvent.Length;
					}
					else
					{
						// No transition, clip starts at end of previous
						info.NewStart = currentPosition;
						currentPosition = info.NewStart + trackEvent.Length;
					}
				}

				trackInfos.Add(info);
			}

			// Phase 1.5: Calculate shifts for clips on other tracks
			List<TrackEventInfo> otherTrackInfos = CalculateOtherTrackShifts(proj, mainTrack, trackInfos);

			// Phase 2: Apply all changes

			// Apply track changes in FORWARD order
			foreach (TrackEventInfo info in trackInfos.OrderBy(i => i.Event.Index))
			{
				info.Event.AdjustStartLength(info.NewStart, info.Event.Length, false);

				// Ensure fade lengths are preserved
				if (info.HasTransition && info.Event.Index > 0)
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
	}
}