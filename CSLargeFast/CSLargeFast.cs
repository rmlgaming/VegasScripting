using ScriptPortal.Vegas;
using CSUtils;

public class EntryPoint
{
    public void FromVegas(Vegas vegas)
    {
        VideoTrack targetTrack = TrackSelection.GetTargetTrack(vegas);
        
        if (targetTrack != null)
        {
            SpeedAdjustment.AdjustSpeedSelectedClips(targetTrack, 3.0);
        }
    }
}