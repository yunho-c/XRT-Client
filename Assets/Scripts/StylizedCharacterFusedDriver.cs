using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class StylizedCharacterFusedDriver : MonoBehaviour
{
    [Header("Sources")]
    [SerializeField] private PoseFusionRouter poseFusionRouter;
    [SerializeField] private Animator targetAnimator;
    [SerializeField] private bool autoFindStylizedCharacter = true;
    [SerializeField] private string stylizedCharacterName = "StylizedCharacter";

    private readonly Dictionary<OVRSkeleton.BoneId, Quaternion> _latestRotations = new Dictionary<OVRSkeleton.BoneId, Quaternion>();
    private readonly Dictionary<OVRSkeleton.BoneId, HumanBodyBones> _boneMap = new Dictionary<OVRSkeleton.BoneId, HumanBodyBones>
    {
        { OVRSkeleton.BoneId.FullBody_Hips, HumanBodyBones.Hips },
        { OVRSkeleton.BoneId.FullBody_SpineLower, HumanBodyBones.Spine },
        { OVRSkeleton.BoneId.FullBody_SpineMiddle, HumanBodyBones.Chest },
        { OVRSkeleton.BoneId.FullBody_Chest, HumanBodyBones.UpperChest },
        { OVRSkeleton.BoneId.FullBody_Neck, HumanBodyBones.Neck },
        { OVRSkeleton.BoneId.FullBody_Head, HumanBodyBones.Head },
        { OVRSkeleton.BoneId.FullBody_LeftShoulder, HumanBodyBones.LeftShoulder },
        { OVRSkeleton.BoneId.FullBody_RightShoulder, HumanBodyBones.RightShoulder },
        { OVRSkeleton.BoneId.FullBody_LeftArmUpper, HumanBodyBones.LeftUpperArm },
        { OVRSkeleton.BoneId.FullBody_RightArmUpper, HumanBodyBones.RightUpperArm },
        { OVRSkeleton.BoneId.FullBody_LeftArmLower, HumanBodyBones.LeftLowerArm },
        { OVRSkeleton.BoneId.FullBody_RightArmLower, HumanBodyBones.RightLowerArm },
        { OVRSkeleton.BoneId.FullBody_LeftHandWrist, HumanBodyBones.LeftHand },
        { OVRSkeleton.BoneId.FullBody_RightHandWrist, HumanBodyBones.RightHand },
        { OVRSkeleton.BoneId.FullBody_LeftUpperLeg, HumanBodyBones.LeftUpperLeg },
        { OVRSkeleton.BoneId.FullBody_RightUpperLeg, HumanBodyBones.RightUpperLeg },
        { OVRSkeleton.BoneId.FullBody_LeftLowerLeg, HumanBodyBones.LeftLowerLeg },
        { OVRSkeleton.BoneId.FullBody_RightLowerLeg, HumanBodyBones.RightLowerLeg },
        { OVRSkeleton.BoneId.FullBody_LeftFootAnkle, HumanBodyBones.LeftFoot },
        { OVRSkeleton.BoneId.FullBody_RightFootAnkle, HumanBodyBones.RightFoot }
    };

    private void Awake()
    {
        if (targetAnimator == null && autoFindStylizedCharacter)
        {
            GameObject stylized = GameObject.Find(stylizedCharacterName);
            if (stylized != null)
            {
                targetAnimator = stylized.GetComponentInChildren<Animator>();
            }
        }
    }

    private void OnEnable()
    {
        if (poseFusionRouter != null)
        {
            poseFusionRouter.OnPoseUpdated += HandlePoseUpdated;
        }
    }

    private void OnDisable()
    {
        if (poseFusionRouter != null)
        {
            poseFusionRouter.OnPoseUpdated -= HandlePoseUpdated;
        }
    }

    private void HandlePoseUpdated(BodyPoseProvider.PoseData poseData)
    {
        if (poseData == null || poseData.bones == null)
        {
            return;
        }

        _latestRotations.Clear();
        for (int i = 0; i < poseData.bones.Count; i++)
        {
            BodyPoseProvider.BoneData bone = poseData.bones[i];
            _latestRotations[bone.id] = bone.rotation;
        }
    }

    private void LateUpdate()
    {
        if (targetAnimator == null || !_latestRotations.TryGetValue(OVRSkeleton.BoneId.FullBody_Hips, out _))
        {
            return;
        }

        foreach (KeyValuePair<OVRSkeleton.BoneId, HumanBodyBones> kvp in _boneMap)
        {
            if (!_latestRotations.TryGetValue(kvp.Key, out Quaternion rotation))
            {
                continue;
            }

            Transform boneTransform = targetAnimator.GetBoneTransform(kvp.Value);
            if (boneTransform == null)
            {
                continue;
            }

            boneTransform.rotation = rotation;
        }
    }
}
