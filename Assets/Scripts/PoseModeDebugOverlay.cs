using UnityEngine;

[DisallowMultipleComponent]
public class PoseModeDebugOverlay : MonoBehaviour
{
    [SerializeField] private PoseFusionRouter poseFusionRouter;
    [SerializeField] private bool logOnModeChange = true;
    [SerializeField] private bool logHeartbeat = false;
    [SerializeField] private float heartbeatIntervalSeconds = 2.0f;

    private bool _hasLastMode;
    private PoseFusionRouter.PoseMode _lastMode;
    private float _nextHeartbeatTime;

    private void Awake()
    {
        if (poseFusionRouter == null)
        {
            poseFusionRouter = GetComponent<PoseFusionRouter>();
        }
    }

    private void Update()
    {
        if (poseFusionRouter == null)
        {
            return;
        }

        PoseFusionRouter.PoseMode mode = poseFusionRouter.CurrentPoseMode;
        if (logOnModeChange && (!_hasLastMode || mode != _lastMode))
        {
            Debug.Log($"[PoseModeDebug] PoseSource: {GetModeLabel(mode)}");
        }

        _hasLastMode = true;
        _lastMode = mode;

        if (logHeartbeat && Time.unscaledTime >= _nextHeartbeatTime)
        {
            Debug.Log($"[PoseModeDebug] PoseSource: {GetModeLabel(mode)}");
            _nextHeartbeatTime = Time.unscaledTime + Mathf.Max(0.25f, heartbeatIntervalSeconds);
        }
    }

    private static string GetModeLabel(PoseFusionRouter.PoseMode mode)
    {
        return mode == PoseFusionRouter.PoseMode.FusedSlimeQuest
            ? "Fused(Slime+Quest)"
            : "QuestOnly";
    }
}
