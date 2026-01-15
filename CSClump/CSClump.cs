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
			if (targetTrack == null)
			{
				System.Windows.Forms.MessageBox.Show("No track selected or no video track found.", "Error");
				return;
			}

			if (targetTrack.Events.Count == 0)
			{
				System.Windows.Forms.MessageBox.Show("Selected track has no events.", "Error");
				return;
			}

			CollapseTracksOptimized(targetTrack);
		}

		private void CollapseTracksOptimized(VideoTrack mainTrack)
		{
			Project proj = mainTrack.Project;
			List<TrackEventInfo> trackInfos = new List<TrackEventInfo>();
			List<MarkerInfo> markerInfos = new List<MarkerInfo>();

			// Phase 1: Calculate all new positions
			Timecode currentPosition = new Timecode(0);

			foreach (TrackEvent trackEvent in mainTrack.Events.OrderBy(te => te.Start))
			{
				TrackEventInfo info = new TrackEventInfo
				{
					Event = trackEvent,
					OriginalStart = trackEvent.Start,
					OriginalEnd = trackEvent.End,
					FadeLength = new Timecode(0)
				};

				if (trackEvent.Index == 0)
				{
					info.NewStart = new Timecode(0);
					currentPosition = trackEvent.Length;
				}
				else
				{
					TrackEventInfo previousInfo = trackInfos[trackEvent.Index - 1];

					// Check if previous clip has a fade out (indicates crossfade)
					Timecode fadeOutLength = new Timecode(0);
					if (previousInfo.Event.FadeOut != null && previousInfo.Event.FadeOut.Length != null)
					{
						fadeOutLength = previousInfo.Event.FadeOut.Length;
					}

					if (fadeOutLength.ToMilliseconds() > 0)
					{
						// Crossfade: overlap by fade length
						info.FadeLength = fadeOutLength;
						Timecode previousNewEnd = previousInfo.NewStart + previousInfo.Event.Length;
						info.NewStart = previousNewEnd - info.FadeLength;
						currentPosition = info.NewStart + trackEvent.Length;
					}
					else
					{
						// No crossfade: consecutive clips
						info.NewStart = currentPosition;
						currentPosition = info.NewStart + trackEvent.Length;
					}
				}

				trackInfos.Add(info);

				// Calculate marker repositions for this track event
				CalculateMarkerRepositions(proj, info, markerInfos);
			}

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

			// Apply track changes in FORWARD order
			foreach (TrackEventInfo info in trackInfos.OrderBy(i => i.Event.Index))
			{
				info.Event.AdjustStartLength(info.NewStart, info.Event.Length, false);

				// Set fade in on current clip to match fade out of previous clip (for crossfade)
				if (info.FadeLength.ToMilliseconds() > 0 && info.Event.Index > 0 && info.Event.FadeIn != null)
				{
					info.Event.FadeIn.Length = info.FadeLength;
				}
			}
		}

		private void CalculateMarkerRepositions(Project proj, TrackEventInfo trackInfo, List<MarkerInfo> markerInfos)
		{
			Timecode shift = trackInfo.NewStart - trackInfo.OriginalStart;

			if (shift.ToMilliseconds() == 0)
				return;

			// Find markers that need to move with this track event
			IEnumerable<Marker> affectedMarkers = proj.Markers.Where(m =>
				m.Position > trackInfo.OriginalStart &&
				m.Position < trackInfo.OriginalEnd &&
				!markerInfos.Any(mi => mi.Marker == m)
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
		}
	}
}