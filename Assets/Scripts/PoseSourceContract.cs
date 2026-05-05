using System.Collections.Generic;
using UnityEngine;

public enum PoseJointSource
{
    Unknown = 0,
    QuestHands = 1,
    SlimeVrcOsc = 2
}

public enum SlimeTrackerRole
{
    Unknown = 0,
    Hip = 1,
    Waist = 2,
    Chest = 3,
    LeftShoulder = 4,
    RightShoulder = 5,
    LeftUpperArm = 6,
    RightUpperArm = 7,
    LeftLowerArm = 8,
    RightLowerArm = 9,
    LeftThigh = 10,
    RightThigh = 11,
    LeftAnkle = 12,
    RightAnkle = 13,
    LeftFoot = 14,
    RightFoot = 15,
    Head = 16
}

public struct SlimeTrackerSample
{
    public SlimeTrackerRole role;
    public Vector3 position;
    public Quaternion rotation;
    public float timestamp;
}

public static class PoseSourceContract
{
    private static readonly HashSet<int> _slimeOwnedBoneIds = new HashSet<int>
    {
        // Torso
        (int)OVRSkeleton.BoneId.FullBody_Hips,
        (int)OVRSkeleton.BoneId.FullBody_SpineLower,
        (int)OVRSkeleton.BoneId.FullBody_SpineMiddle,
        (int)OVRSkeleton.BoneId.FullBody_SpineUpper,
        (int)OVRSkeleton.BoneId.FullBody_Chest,
        // Arms
        (int)OVRSkeleton.BoneId.FullBody_LeftShoulder,
        (int)OVRSkeleton.BoneId.FullBody_RightShoulder,
        (int)OVRSkeleton.BoneId.FullBody_LeftArmUpper,
        (int)OVRSkeleton.BoneId.FullBody_RightArmUpper,
        (int)OVRSkeleton.BoneId.FullBody_LeftArmLower,
        (int)OVRSkeleton.BoneId.FullBody_RightArmLower,
        // Legs
        (int)OVRSkeleton.BoneId.FullBody_LeftUpperLeg,
        (int)OVRSkeleton.BoneId.FullBody_RightUpperLeg,
        (int)OVRSkeleton.BoneId.FullBody_LeftLowerLeg,
        (int)OVRSkeleton.BoneId.FullBody_RightLowerLeg,
        (int)OVRSkeleton.BoneId.FullBody_LeftFootAnkle,
        (int)OVRSkeleton.BoneId.FullBody_RightFootAnkle
    };

    private static readonly HashSet<OVRSkeleton.BoneId> _questHandBones = new HashSet<OVRSkeleton.BoneId>
    {
        OVRSkeleton.BoneId.FullBody_LeftHandPalm,
        OVRSkeleton.BoneId.FullBody_LeftHandWrist,
        OVRSkeleton.BoneId.FullBody_RightHandPalm,
        OVRSkeleton.BoneId.FullBody_RightHandWrist,
        OVRSkeleton.BoneId.Body_LeftHandPalm,
        OVRSkeleton.BoneId.Body_LeftHandWrist,
        OVRSkeleton.BoneId.Body_RightHandPalm,
        OVRSkeleton.BoneId.Body_RightHandWrist
    };

    public static bool IsQuestHandBone(OVRSkeleton.BoneId boneId)
    {
        return _questHandBones.Contains(boneId) || IsFingerBone(boneId);
    }

    public static bool IsFingerBone(OVRSkeleton.BoneId boneId)
    {
        int id = (int)boneId;
        return (id >= (int)OVRSkeleton.BoneId.FullBody_LeftHandThumbMetacarpal && id <= (int)OVRSkeleton.BoneId.FullBody_RightHandLittleTip)
            || (id >= (int)OVRSkeleton.BoneId.Body_LeftHandThumbMetacarpal && id <= (int)OVRSkeleton.BoneId.Body_RightHandLittleTip);
    }

    public static bool IsSlimeOwnedBodyBone(OVRSkeleton.BoneId boneId)
    {
        if (IsQuestHandBone(boneId))
        {
            return false;
        }

        return _slimeOwnedBoneIds.Contains((int)boneId);
    }

    public static PoseJointSource GetPreferredSource(OVRSkeleton.BoneId boneId)
    {
        if (IsQuestHandBone(boneId))
        {
            return PoseJointSource.QuestHands;
        }

        if (IsSlimeOwnedBodyBone(boneId))
        {
            return PoseJointSource.SlimeVrcOsc;
        }

        return PoseJointSource.Unknown;
    }

    public static bool IsLowerBodyBone(OVRSkeleton.BoneId boneId)
    {
        switch (boneId)
        {
            case OVRSkeleton.BoneId.FullBody_Hips:
            case OVRSkeleton.BoneId.FullBody_LeftUpperLeg:
            case OVRSkeleton.BoneId.FullBody_RightUpperLeg:
            case OVRSkeleton.BoneId.FullBody_LeftLowerLeg:
            case OVRSkeleton.BoneId.FullBody_RightLowerLeg:
            case OVRSkeleton.BoneId.FullBody_LeftFootAnkle:
            case OVRSkeleton.BoneId.FullBody_RightFootAnkle:
                return true;
            default:
                return false;
        }
    }
}
