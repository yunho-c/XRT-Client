using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class PoseFusionRouter : MonoBehaviour
{
    public enum PoseMode
    {
        QuestOnly = 0,
        FusedSlimeQuest = 1
    }

    [Header("Sources")]
    [SerializeField] private BodyPoseProvider questBodyPoseProvider;
    [SerializeField] private SlimeBodyMapper slimeBodyMapper;
    [SerializeField] private bool publishOnlyWhenSlimeHealthy = false;
    [SerializeField] private bool useBasePoseWhenLowerBodyTrackersMissing = true;

    public BodyPoseProvider.PoseData CurrentPoseData { get; private set; }
    public PoseMode CurrentPoseMode { get; private set; } = PoseMode.QuestOnly;
    public bool IsFusedActive => CurrentPoseMode == PoseMode.FusedSlimeQuest;
    public event Action<BodyPoseProvider.PoseData> OnPoseUpdated;

    private readonly List<BodyPoseProvider.BoneData> _workingBones = new List<BodyPoseProvider.BoneData>();
    private readonly List<BodyPoseProvider.BoneData> _basePoseBones = new List<BodyPoseProvider.BoneData>();
    private bool _basePoseCaptured;

    private void OnEnable()
    {
        if (questBodyPoseProvider != null)
        {
            questBodyPoseProvider.OnPoseUpdated += HandleQuestPoseUpdated;
        }
    }

    private void OnDisable()
    {
        if (questBodyPoseProvider != null)
        {
            questBodyPoseProvider.OnPoseUpdated -= HandleQuestPoseUpdated;
        }
    }

    private void HandleQuestPoseUpdated(BodyPoseProvider.PoseData questPose)
    {
        if (questPose == null || questPose.bones == null)
        {
            return;
        }

        if (CurrentPoseData == null)
        {
            CurrentPoseData = new BodyPoseProvider.PoseData();
        }

        CurrentPoseData.timestamp = questPose.timestamp;
        ResizeWorkingBonesIfNeeded(questPose.bones.Count);
        CaptureBasePoseIfNeeded(questPose);

        for (int i = 0; i < questPose.bones.Count; i++)
        {
            _workingBones[i] = questPose.bones[i];
        }

        bool canApplySlime = slimeBodyMapper != null
                             && slimeBodyMapper.HasFreshFrame
                             && slimeBodyMapper.CurrentMappedBones != null;
        CurrentPoseMode = PoseMode.QuestOnly;

        if (!publishOnlyWhenSlimeHealthy || canApplySlime)
        {
            if (canApplySlime)
            {
                ApplySlimeOverrides(_workingBones, slimeBodyMapper.CurrentMappedBones);
                ApplyFallbackPolicies(_workingBones);
                if (slimeBodyMapper.CurrentMappedBones.Count > 0)
                {
                    CurrentPoseMode = PoseMode.FusedSlimeQuest;
                }
            }
            else if (publishOnlyWhenSlimeHealthy)
            {
                return;
            }
        }

        CurrentPoseData.bones.Clear();
        CurrentPoseData.bones.AddRange(_workingBones);
        OnPoseUpdated?.Invoke(CurrentPoseData);
    }

    private void CaptureBasePoseIfNeeded(BodyPoseProvider.PoseData questPose)
    {
        if (_basePoseCaptured)
        {
            return;
        }

        _basePoseBones.Clear();
        _basePoseBones.AddRange(questPose.bones);
        _basePoseCaptured = _basePoseBones.Count > 0;
    }

    private void ApplySlimeOverrides(
        List<BodyPoseProvider.BoneData> targetBones,
        IReadOnlyDictionary<OVRSkeleton.BoneId, BodyPoseProvider.BoneData> slimeOverrides)
    {
        foreach (KeyValuePair<OVRSkeleton.BoneId, BodyPoseProvider.BoneData> kvp in slimeOverrides)
        {
            OVRSkeleton.BoneId boneId = kvp.Key;
            if (!PoseSourceContract.IsSlimeOwnedBodyBone(boneId))
            {
                continue;
            }

            int index = (int)boneId;
            if (index < 0 || index >= targetBones.Count)
            {
                continue;
            }

            BodyPoseProvider.BoneData value = kvp.Value;
            value.id = targetBones[index].id;
            targetBones[index] = value;
        }
    }

    private void ApplyFallbackPolicies(List<BodyPoseProvider.BoneData> targetBones)
    {
        if (slimeBodyMapper == null)
        {
            return;
        }

        // If no lower-body tracker data is connected, hold lower-body to base pose (instead of Quest legs).
        if (useBasePoseWhenLowerBodyTrackersMissing && _basePoseCaptured && !slimeBodyMapper.HasLowerBodyTrackers)
        {
            int max = Mathf.Min(targetBones.Count, _basePoseBones.Count);
            for (int i = 0; i < max; i++)
            {
                OVRSkeleton.BoneId boneId = targetBones[i].id;
                if (PoseSourceContract.IsLowerBodyBone(boneId))
                {
                    BodyPoseProvider.BoneData value = _basePoseBones[i];
                    value.id = boneId;
                    targetBones[i] = value;
                }
            }
        }

        // Shoulders/elbows/wrists should fall back to Quest when arm trackers are absent.
        // Wrists are already Quest-owned by PoseSourceContract; this explicitly guards shoulders/elbows.
        if (_basePoseCaptured && !slimeBodyMapper.HasArmTrackers)
        {
            // No arm-specific Slime override gets applied in this case, so quest values already remain.
            // Intentionally left as a no-op for clarity and future debugging.
        }
    }

    private void ResizeWorkingBonesIfNeeded(int targetCount)
    {
        if (_workingBones.Count == targetCount)
        {
            return;
        }

        _workingBones.Clear();
        for (int i = 0; i < targetCount; i++)
        {
            _workingBones.Add(new BodyPoseProvider.BoneData());
        }
    }
}
