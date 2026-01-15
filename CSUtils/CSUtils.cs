using ScriptPortal.Vegas;
using System;
using System.Collections.Generic;

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

			foreach (TrackEvent trackEvent in track.Events)
			{
				if (!trackEvent.Selected)
				{
					continue;
				}

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

				foreach (Marker m in transitionMarkers)
				{
					Timecode markerRelativePosition = m.Position - trackEvent.Start;
					Timecode newRelativePosition = new Timecode(markerRelativePosition.ToMilliseconds() / actualSpeedModification);
					Timecode newPosition = trackEvent.Start + RoundRandomToFrame(newRelativePosition, random);
					MoveMarker(track.Project, m, newPosition);
				}
			}
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