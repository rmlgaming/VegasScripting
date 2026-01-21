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
		}

		public void FromVegas(Vegas vegas)
		{
			VideoTrack targetTrack = TrackSelection.GetTargetTrack(vegas);
			Timecode cursorPosition = vegas.Transport.CursorPosition;
			Timecode shiftAmount = Timecode.FromSeconds(4.0);

			ShiftClipsAfterCursor(targetTrack, vegas.Project, cursorPosition, shiftAmount);
		}

		private void ShiftClipsAfterCursor(VideoTrack mainTrack, Project proj, Timecode cursorPos, Timecode shiftAmount)
		{
			List<TrackEventInfo> mainTrackInfos = new List<TrackEventInfo>();

			// Phase 1: Calculate new positions for main track clips after cursor
			foreach (TrackEvent trackEvent in mainTrack.Events.OrderBy(te => te.Start))
			{
				TrackEventInfo info = new TrackEventInfo
				{
					Event = trackEvent,
					OriginalStart = trackEvent.Start,
					OriginalEnd = trackEvent.End
				};

				// Only shift clips that start at or after the cursor position
				if (trackEvent.Start >= cursorPos)
				{
					info.NewStart = trackEvent.Start + shiftAmount;
				}
				else
				{
					// Clips before cursor stay in place
					info.NewStart = trackEvent.Start;
				}

				mainTrackInfos.Add(info);
			}

			// Phase 2: Calculate shifts for clips on other tracks
			List<TrackEventInfo> otherTrackInfos = CalculateOtherTrackShifts(proj, mainTrack, mainTrackInfos);

			// Phase 3: Apply all changes

			// Apply main track changes
			foreach (TrackEventInfo info in mainTrackInfos)
			{
				if (info.NewStart != info.OriginalStart)
				{
					info.Event.AdjustStartLength(info.NewStart, info.Event.Length, false);
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