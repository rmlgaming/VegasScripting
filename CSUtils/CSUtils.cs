using ScriptPortal.Vegas;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CSUtils
{
	public static class TrackSelection
	{
		public static VideoTrack GetTargetTrack(Vegas vegas)
		{
			// First, look for a video track named "main" (case-insensitive)
			foreach (Track track in vegas.Project.Tracks)
			{
				if (track.IsVideo() &&
					track.Name != null &&
					track.Name.ToLower() == "main")
				{
					return (VideoTrack)track;
				}
			}

			throw new Exception("No video track named 'main' found.");
		}
	}

	public static class SpeedAdjustment
	{
		static double MAX_SPEED = 4.0;
		static double MIN_SPEED = 0.25;

		public static void AdjustSpeedSelectedClips(Track track, double speedModification)
		{
			Random random = new Random();

			// Collect timing adjustments - these will cascade
			List<TimingAdjustment> timingAdjustments = new List<TimingAdjustment>();

			foreach (TrackEvent trackEvent in track.Events)
			{
				if (!trackEvent.Selected)
				{
					continue;
				}

				Timecode oldStart = trackEvent.Start;
				Timecode oldEnd = trackEvent.End;
				Timecode oldLength = trackEvent.Length;

				Timecode newLength = new Timecode(oldLength.ToMilliseconds() / speedModification);
				Timecode newLengthRounded = RoundRandomToFrameWithoutRedundancy(newLength, oldLength, random);

				double actualLengthModification = newLengthRounded.ToMilliseconds() / oldLength.ToMilliseconds();
				double actualSpeedModification = 1.0 / actualLengthModification;

				List<Marker> transitionMarkers = new List<Marker>();
				foreach (Marker m in trackEvent.Project.Markers)
				{
					if (IsTransitionMarker(m) && MarkerIsWithinTrackEvent(m, trackEvent))
					{
						transitionMarkers.Add(m);
					}
				}

				VideoEvent videoEvent = (VideoEvent)trackEvent;
				Envelopes envelopes = videoEvent.Envelopes;
				Envelope velocity = envelopes.HasEnvelope(EnvelopeType.Velocity)
					? envelopes.FindByType(EnvelopeType.Velocity)
					: null;

				double velocityFactor = velocity == null ? 1 : velocity.Points[0].Y;
				double currentVelocity = trackEvent.PlaybackRate * velocityFactor;
				double desiredPlaybackRate = currentVelocity * actualSpeedModification;

				if (desiredPlaybackRate > MAX_SPEED)
				{
					double y = desiredPlaybackRate / MAX_SPEED;
					desiredPlaybackRate = MAX_SPEED;
					if (velocity == null)
					{
						velocity = new Envelope(EnvelopeType.Velocity);
						videoEvent.Envelopes.Add(velocity);
					}
					velocity.Points[0].Y = y;
				}
				else if (desiredPlaybackRate < MIN_SPEED)
				{
					double y = desiredPlaybackRate / MIN_SPEED;
					desiredPlaybackRate = MIN_SPEED;
					if (velocity == null)
					{
						velocity = new Envelope(EnvelopeType.Velocity);
						videoEvent.Envelopes.Add(velocity);
					}
					velocity.Points[0].Y = y;
				}
				else if (velocity != null)
				{
					videoEvent.Envelopes.Remove(velocity);
				}

				trackEvent.AdjustPlaybackRate(desiredPlaybackRate, true);
				trackEvent.Length = newLengthRounded;

				// Calculate new end position
				Timecode newEnd = trackEvent.Start + newLengthRounded;

				// Store the adjustment info
				timingAdjustments.Add(new TimingAdjustment
				{
					TrackEvent = trackEvent,
					OldStart = oldStart,
					OldEnd = oldEnd,
					NewStart = oldStart,
					NewEnd = newEnd
				});

				foreach (Marker m in transitionMarkers)
				{
					Timecode markerRelativePosition = m.Position - trackEvent.Start;
					Timecode newRelativePosition = new Timecode(markerRelativePosition.ToMilliseconds() / actualSpeedModification);
					Timecode newPosition = trackEvent.Start + RoundRandomToFrame(newRelativePosition, random);
					MoveMarker(track.Project, m, newPosition);
				}
			}

			// Now cascade adjustments to main track and then to other tracks
			CascadeAdjustments(track, timingAdjustments);
		}

		private static void CascadeAdjustments(Track mainTrack, List<TimingAdjustment> initialAdjustments)
		{
			// First, cascade on the main track itself
			HashSet<TrackEvent> processedMainEvents = new HashSet<TrackEvent>();
			foreach (var adj in initialAdjustments)
			{
				processedMainEvents.Add(adj.TrackEvent);
			}

			// Get all events on main track sorted by start time
			var mainTrackEvents = mainTrack.Events
				.Cast<TrackEvent>()
				.Where(e => !processedMainEvents.Contains(e))
				.OrderBy(e => e.Start.ToMilliseconds())
				.ToList();

			// Process main track events in order, building up adjustments list
			foreach (var trackEvent in mainTrackEvents)
			{
				Timecode eventStart = trackEvent.Start;
				Timecode oldStart = trackEvent.Start;

				// Check if this clip overlaps with any adjusted clip
				foreach (TimingAdjustment adj in initialAdjustments)
				{
					if (eventStart >= adj.OldStart && eventStart <= adj.OldEnd)
					{
						// Calculate proportional position
						double relativePosition = (eventStart.ToMilliseconds() - adj.OldStart.ToMilliseconds()) /
												 (adj.OldEnd.ToMilliseconds() - adj.OldStart.ToMilliseconds());

						// Calculate new start
						double newClipLength = adj.NewEnd.ToMilliseconds() - adj.NewStart.ToMilliseconds();
						double newStartMs = adj.NewStart.ToMilliseconds() + (relativePosition * newClipLength);
						Timecode newStart = new Timecode(newStartMs);

						// Move the clip
						Timecode oldEnd = trackEvent.End;
						trackEvent.Start = newStart;
						Timecode newEnd = trackEvent.End;

						// Add this as a new adjustment for cascading
						initialAdjustments.Add(new TimingAdjustment
						{
							TrackEvent = trackEvent,
							OldStart = oldStart,
							OldEnd = oldEnd,
							NewStart = newStart,
							NewEnd = newEnd
						});

						processedMainEvents.Add(trackEvent);
						break;
					}
				}
			}

			// Now apply to all other tracks
			ApplyAdjustmentsToOtherTracks(mainTrack.Project, initialAdjustments);
		}

		private static void ApplyAdjustmentsToOtherTracks(Project project, List<TimingAdjustment> adjustments)
		{
			// Build groups of events
			Dictionary<TrackEvent, List<TrackEvent>> eventGroups = BuildEventGroups(project);
			HashSet<TrackEvent> processedEvents = new HashSet<TrackEvent>();

			foreach (Track track in project.Tracks)
			{
				// Skip "main" and "music" tracks
				if (track.Name != null)
				{
					string trackNameLower = track.Name.ToLower();
					if (trackNameLower == "main" || trackNameLower == "music")
					{
						continue;
					}
				}

				foreach (TrackEvent trackEvent in track.Events)
				{
					if (processedEvents.Contains(trackEvent))
					{
						continue;
					}

					// Get all events in this group
					List<TrackEvent> groupEvents = eventGroups.ContainsKey(trackEvent)
						? eventGroups[trackEvent]
						: new List<TrackEvent> { trackEvent };

					// Find the earliest event in the group
					TrackEvent earliestEvent = groupEvents.OrderBy(e => e.Start.ToMilliseconds()).First();
					Timecode groupAnchorStart = earliestEvent.Start;

					// Find matching adjustment
					TimingAdjustment matchingAdjustment = null;
					double relativePosition = 0;

					foreach (TimingAdjustment adj in adjustments)
					{
						double anchorMs = groupAnchorStart.ToMilliseconds();
						double oldStartMs = adj.OldStart.ToMilliseconds();
						double oldEndMs = adj.OldEnd.ToMilliseconds();

						if (anchorMs >= oldStartMs && anchorMs <= oldEndMs)
						{
							double oldLengthMs = oldEndMs - oldStartMs;
							relativePosition = (anchorMs - oldStartMs) / oldLengthMs;
							matchingAdjustment = adj;
							break;
						}
					}

					if (matchingAdjustment != null)
					{
						// Calculate new position
						double newClipLength = matchingAdjustment.NewEnd.ToMilliseconds() - matchingAdjustment.NewStart.ToMilliseconds();
						double newAnchorStartMs = matchingAdjustment.NewStart.ToMilliseconds() + (relativePosition * newClipLength);
						Timecode newAnchorStart = new Timecode(newAnchorStartMs);

						// Calculate offset
						double offsetMs = newAnchorStart.ToMilliseconds() - groupAnchorStart.ToMilliseconds();

						// Apply to all events in group
						foreach (TrackEvent evt in groupEvents)
						{
							Timecode newStart = new Timecode(evt.Start.ToMilliseconds() + offsetMs);
							evt.Start = newStart;
							processedEvents.Add(evt);
						}
					}
					else
					{
						foreach (TrackEvent evt in groupEvents)
						{
							processedEvents.Add(evt);
						}
					}
				}
			}
		}

		private static Dictionary<TrackEvent, List<TrackEvent>> BuildEventGroups(Project project)
		{
			Dictionary<TrackEvent, List<TrackEvent>> groups = new Dictionary<TrackEvent, List<TrackEvent>>();
			HashSet<TrackEvent> assignedEvents = new HashSet<TrackEvent>();

			foreach (Track track in project.Tracks)
			{
				// Skip "main" and "music" tracks
				if (track.Name != null)
				{
					string trackNameLower = track.Name.ToLower();
					if (trackNameLower == "main" || trackNameLower == "music")
					{
						continue;
					}
				}

				foreach (TrackEvent trackEvent in track.Events)
				{
					if (assignedEvents.Contains(trackEvent))
					{
						continue;
					}

					// Find all events in the same group
					List<TrackEvent> groupMembers = new List<TrackEvent>();
					CollectGroupMembers(trackEvent, groupMembers, assignedEvents);

					// Assign this group to all members
					foreach (TrackEvent member in groupMembers)
					{
						groups[member] = groupMembers;
						assignedEvents.Add(member);
					}
				}
			}

			return groups;
		}

		private static void CollectGroupMembers(TrackEvent startEvent, List<TrackEvent> groupMembers, HashSet<TrackEvent> assignedEvents)
		{
			if (assignedEvents.Contains(startEvent) || groupMembers.Contains(startEvent))
			{
				return;
			}

			groupMembers.Add(startEvent);

			if (startEvent.Group != null)
			{
				foreach (Track track in startEvent.Project.Tracks)
				{
					foreach (TrackEvent evt in track.Events)
					{
						if (evt.Group == startEvent.Group && !groupMembers.Contains(evt))
						{
							CollectGroupMembers(evt, groupMembers, assignedEvents);
						}
					}
				}
			}
		}

		private class TimingAdjustment
		{
			public TrackEvent TrackEvent { get; set; }
			public Timecode OldStart { get; set; }
			public Timecode OldEnd { get; set; }
			public Timecode NewStart { get; set; }
			public Timecode NewEnd { get; set; }
		}

		public static bool IsTransitionMarker(Marker m)
		{
			return m.Label.ToString().ToLower() == "v";
		}

		public static bool MarkerIsWithinTrackEvent(Marker m, TrackEvent te)
		{
			return m.Position >= te.Start && m.Position < te.End;
		}

		public static Timecode RoundDownToFrame(Timecode timecode)
		{
			return Timecode.FromFrames(timecode.FrameCount);
		}

		public static Timecode RoundUpToFrame(Timecode timecode)
		{
			return Timecode.FromFrames(timecode.FrameCount + 1);
		}

		public static Timecode RoundRandomToFrame(Timecode timecode, Random random)
		{
			return Timecode.FromFrames(timecode.FrameCount + random.Next(2));
		}

		public static Timecode RoundRandomToFrameWithoutRedundancy(Timecode newLength, Timecode oldLength, Random random)
		{
			if (RoundDownToFrame(newLength) == oldLength || RoundDownToFrame(newLength) == new Timecode())
			{
				return RoundUpToFrame(newLength);
			}
			else if (RoundUpToFrame(newLength) == oldLength)
			{
				return RoundDownToFrame(newLength);
			}
			else
			{
				return RoundRandomToFrame(newLength, random);
			}
		}

		public static void MoveMarker(Project proj, Marker marker, Timecode newPosition)
		{
			string label = marker.Label.ToString();
			proj.Markers.Remove(marker);
			proj.Markers.Add(new Marker(newPosition, label));
		}
	}
}