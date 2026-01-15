using ScriptPortal.Vegas;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.IO;
using System.Globalization;

namespace VegasScripting
{
	public class EntryPoint
	{
		public void FromVegas(Vegas vegas)
		{
			OpenFileDialog openFileDialog = new OpenFileDialog();
			openFileDialog.InitialDirectory = "C:\\Users\\rlind\\OneDrive\\Documents\\Github\\auto_edit\\outputs";
			openFileDialog.Filter = "CSV Files (*csv)|*.csv";
			openFileDialog.FilterIndex = 0;
			openFileDialog.RestoreDirectory = true;

			if (openFileDialog.ShowDialog() == DialogResult.OK)
			{
				string selectedFileName = openFileDialog.FileName;

				using (var reader = new StreamReader(selectedFileName))
				{
					Track mainTrack;
					IEnumerable<Track> yeetTracks = vegas.Project.Tracks.Where(t => t.Name == "yeet");
					if (yeetTracks.Count() > 0)
					{
						mainTrack = yeetTracks.First();
					}
					else
					{
						mainTrack = vegas.Project.Tracks[0];
					}

					Timecode previousTimecode = new Timecode();

					// Skip header line if present
					if (!reader.EndOfStream)
					{
						string firstLine = reader.ReadLine();
						// Check if it's a header (contains non-numeric data in second column)
						string[] testValues = firstLine.Split(',');
						float testFloat;
						if (!float.TryParse(testValues[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out testFloat))
						{
							// It's a header, continue to next line
						}
						else
						{
							// It's data, process it
							ProcessLine(firstLine, mainTrack, ref previousTimecode);
						}
					}

					while (!reader.EndOfStream)
					{
						string line = reader.ReadLine();
						ProcessLine(line, mainTrack, ref previousTimecode);
					}
				}
			}
		}

		private void ProcessLine(string line, Track mainTrack, ref Timecode previousTimecode)
		{
			if (string.IsNullOrWhiteSpace(line)) return;

			string[] values = line.Split(',');
			if (values.Length < 2) return;

			string command = values[0].Trim();

			// Parse timestamp with better error handling
			float timestamp;
			if (!float.TryParse(values[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out timestamp))
			{
				// If parsing fails, try with current culture
				if (!float.TryParse(values[1].Trim(), out timestamp))
				{
					return; // Skip this line if we can't parse the timestamp
				}
			}

			if (timestamp == 0) return;

			TrackEvent trackEvent = EventAtTimestamp(timestamp, mainTrack);
			if (trackEvent == null) return; // No event found at this timestamp

			Timecode timecode = new Timecode(timestamp);
			trackEvent.Split(timecode - trackEvent.Start);

			if (command == "X")
			{
				int trackEventIndex = trackEvent.Index;
				mainTrack.Events.Remove(trackEvent);

				// Remove hanging track
				if (trackEventIndex > 0)
				{
					TrackEvent previousTrackEvent = mainTrack.Events.ElementAt(trackEventIndex - 1);
					if (previousTimecode == previousTrackEvent.Start)
					{
						mainTrack.Events.Remove(previousTrackEvent);
					}
				}
			}
			else if (command == "F")
			{
				trackEvent.AdjustPlaybackRate(3, true);
				trackEvent.Length = new Timecode(trackEvent.Length.ToMilliseconds() / 3);
			}

			previousTimecode = timecode;
		}

		public TrackEvent EventAtTimestamp(float ts, Track t)
		{
			foreach (TrackEvent e in t.Events)
			{
				if (ts >= e.Start.ToMilliseconds() && ts < e.End.ToMilliseconds())
				{
					return e;
				}
			}
			return null;
		}
	}
}