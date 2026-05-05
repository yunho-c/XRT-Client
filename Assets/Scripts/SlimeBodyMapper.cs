using System;
using System.Collections.Generic;
using UnityEngine;

public class SlimeBodyMapper : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private SlimeVmcReceiver vmcReceiver;
    [SerializeField] private Transform headingReference;

    [Header("Calibration")]
    [SerializeField] private float hipHeightMeters = 1.0f;
    [SerializeField] private float floorOffsetMeters = 0.0f;
    [SerializeField] private float smoothingStrength = 0.18f;

    private readonly Dictionary<OVRSkeleton.BoneId, BodyPoseProvider.BoneData> _mappedBones = new Dictionary<OVRSkeleton.BoneId, BodyPoseProvider.BoneData>();
    private readonly Dictionary<OVRSkeleton.BoneId, Quaternion> _smoothedRotations = new Dictionary<OVRSkeleton.BoneId, Quaternion>();
    private readonly Dictionary<OVRSkeleton.BoneId, Vector3> _smoothedPositions = new Dictionary<OVRSkeleton.BoneId, Vector3>();
    private Quaternion _yawOffset = Quaternion.identity;
    private bool _hasFrame;
    private bool _hasArmTrackers;
    private bool _hasLowerBodyTrackers;

    public bool HasFreshFrame => _hasFrame && vmcReceiver != null && vmcReceiver.IsStreamHealthy;
    public bool HasArmTrackers => _hasArmTrackers;
    public bool HasLowerBodyTrackers => _hasLowerBodyTrackers;

    public IReadOnlyDictionary<OVRSkeleton.BoneId, BodyPoseProvider.BoneData> CurrentMappedBones => _mappedBones;

    private void OnEnable()
    {
        if (vmcReceiver != null)
        {
            vmcReceiver.OnTrackerFrame += HandleTrackerFrame;
        }
    }

    private void OnDisable()
    {
        if (vmcReceiver != null)
        {
            vmcReceiver.OnTrackerFrame -= HandleTrackerFrame;
        }
    }

    public void SetForward()
    {
        if (headingReference == null)
        {
            return;
        }

        Vector3 forward = headingReference.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
        {
            return;
        }

        _yawOffset = Quaternion.Inverse(Quaternion.LookRotation(forward.normalized, Vector3.up));
    }

    public void ResetYaw()
    {
        _yawOffset = Quaternion.identity;
    }

    public void SetHipHeight(float meters)
    {
        hipHeightMeters = Mathf.Max(0.1f, meters);
    }

    public void SetFloorOffset(float meters)
    {
        floorOffsetMeters = meters;
    }

    private void HandleTrackerFrame(IReadOnlyDictionary<SlimeTrackerRole, SlimeTrackerSample> frame)
    {
        _mappedBones.Clear();
        _hasFrame = frame != null && frame.Count > 0;
        _hasArmTrackers = false;
        _hasLowerBodyTrackers = false;
        if (!_hasFrame)
        {
            return;
        }

        Vector3 hipsPosition = new Vector3(0f, hipHeightMeters + floorOffsetMeters, 0f);
        Quaternion chestRotation = Quaternion.identity;
        Quaternion hipsRotation = Quaternion.identity;

        if (TryGetRole(frame, SlimeTrackerRole.Hip, out SlimeTrackerSample hip))
        {
            hipsPosition = ConvertPosition(hip.position);
            hipsRotation = ConvertRotation(hip.rotation);
            hipsPosition.y = hipHeightMeters + floorOffsetMeters;
            SetMappedBone(OVRSkeleton.BoneId.FullBody_Hips, hipsPosition, hipsRotation, true);
        }
        else if (TryGetRole(frame, SlimeTrackerRole.Waist, out SlimeTrackerSample waist))
        {
            hipsPosition = ConvertPosition(waist.position);
            hipsRotation = ConvertRotation(waist.rotation);
            hipsPosition.y = hipHeightMeters + floorOffsetMeters;
            SetMappedBone(OVRSkeleton.BoneId.FullBody_Hips, hipsPosition, hipsRotation, true);
        }

        if (TryGetRole(frame, SlimeTrackerRole.Waist, out SlimeTrackerSample waistSpine))
        {
            Quaternion waistRotation = ConvertRotation(waistSpine.rotation);
            Vector3 waistPosition = ConvertPosition(waistSpine.position);
            waistPosition.y = Mathf.Max(waistPosition.y, hipHeightMeters + 0.08f);
            SetMappedBone(OVRSkeleton.BoneId.FullBody_SpineLower, waistPosition, waistRotation, true);
            SetMappedBone(OVRSkeleton.BoneId.FullBody_SpineMiddle, waistPosition + (Vector3.up * 0.1f), waistRotation, false);
        }

        if (TryGetRole(frame, SlimeTrackerRole.Chest, out SlimeTrackerSample chest))
        {
            Vector3 chestPosition = ConvertPosition(chest.position);
            chestPosition.y = Mathf.Max(chestPosition.y, hipHeightMeters + 0.2f);
            chestRotation = ConvertRotation(chest.rotation);
            SetMappedBone(OVRSkeleton.BoneId.FullBody_Chest, chestPosition, chestRotation, true);
            SetMappedBone(OVRSkeleton.BoneId.FullBody_SpineUpper, chestPosition + (Vector3.down * 0.08f), chestRotation, false);
        }

        if (TryGetRole(frame, SlimeTrackerRole.LeftShoulder, out SlimeTrackerSample leftShoulder))
        {
            _hasArmTrackers = true;
            SetMappedBone(OVRSkeleton.BoneId.FullBody_LeftShoulder, ConvertPosition(leftShoulder.position), ConvertRotation(leftShoulder.rotation), false);
        }
        if (TryGetRole(frame, SlimeTrackerRole.RightShoulder, out SlimeTrackerSample rightShoulder))
        {
            _hasArmTrackers = true;
            SetMappedBone(OVRSkeleton.BoneId.FullBody_RightShoulder, ConvertPosition(rightShoulder.position), ConvertRotation(rightShoulder.rotation), false);
        }

        if (TryGetRole(frame, SlimeTrackerRole.LeftUpperArm, out SlimeTrackerSample leftUpperArm))
        {
            _hasArmTrackers = true;
            SetMappedBone(OVRSkeleton.BoneId.FullBody_LeftArmUpper, ConvertPosition(leftUpperArm.position), ConvertRotation(leftUpperArm.rotation), false);
        }
        if (TryGetRole(frame, SlimeTrackerRole.RightUpperArm, out SlimeTrackerSample rightUpperArm))
        {
            _hasArmTrackers = true;
            SetMappedBone(OVRSkeleton.BoneId.FullBody_RightArmUpper, ConvertPosition(rightUpperArm.position), ConvertRotation(rightUpperArm.rotation), false);
        }

        if (TryGetRole(frame, SlimeTrackerRole.LeftLowerArm, out SlimeTrackerSample leftLowerArm))
        {
            _hasArmTrackers = true;
            SetMappedBone(OVRSkeleton.BoneId.FullBody_LeftArmLower, ConvertPosition(leftLowerArm.position), ConvertRotation(leftLowerArm.rotation), false);
        }
        if (TryGetRole(frame, SlimeTrackerRole.RightLowerArm, out SlimeTrackerSample rightLowerArm))
        {
            _hasArmTrackers = true;
            SetMappedBone(OVRSkeleton.BoneId.FullBody_RightArmLower, ConvertPosition(rightLowerArm.position), ConvertRotation(rightLowerArm.rotation), false);
        }

        if (TryGetRole(frame, SlimeTrackerRole.LeftThigh, out SlimeTrackerSample leftThigh))
        {
            _hasLowerBodyTrackers = true;
            SetMappedBone(OVRSkeleton.BoneId.FullBody_LeftUpperLeg, ConvertPosition(leftThigh.position), ConvertRotation(leftThigh.rotation), false);
        }
        if (TryGetRole(frame, SlimeTrackerRole.RightThigh, out SlimeTrackerSample rightThigh))
        {
            _hasLowerBodyTrackers = true;
            SetMappedBone(OVRSkeleton.BoneId.FullBody_RightUpperLeg, ConvertPosition(rightThigh.position), ConvertRotation(rightThigh.rotation), false);
        }
        if (TryGetRole(frame, SlimeTrackerRole.LeftAnkle, out SlimeTrackerSample leftAnkle))
        {
            _hasLowerBodyTrackers = true;
            SetMappedBone(OVRSkeleton.BoneId.FullBody_LeftLowerLeg, ConvertPosition(leftAnkle.position), ConvertRotation(leftAnkle.rotation), false);
        }
        if (TryGetRole(frame, SlimeTrackerRole.RightAnkle, out SlimeTrackerSample rightAnkle))
        {
            _hasLowerBodyTrackers = true;
            SetMappedBone(OVRSkeleton.BoneId.FullBody_RightLowerLeg, ConvertPosition(rightAnkle.position), ConvertRotation(rightAnkle.rotation), false);
        }
        if (TryGetRole(frame, SlimeTrackerRole.LeftFoot, out SlimeTrackerSample leftFoot))
        {
            _hasLowerBodyTrackers = true;
            SetMappedBone(OVRSkeleton.BoneId.FullBody_LeftFootAnkle, ConvertPosition(leftFoot.position), ConvertRotation(leftFoot.rotation), true);
        }
        if (TryGetRole(frame, SlimeTrackerRole.RightFoot, out SlimeTrackerSample rightFoot))
        {
            _hasLowerBodyTrackers = true;
            SetMappedBone(OVRSkeleton.BoneId.FullBody_RightFootAnkle, ConvertPosition(rightFoot.position), ConvertRotation(rightFoot.rotation), true);
        }
    }

    private bool TryGetRole(IReadOnlyDictionary<SlimeTrackerRole, SlimeTrackerSample> frame, SlimeTrackerRole role, out SlimeTrackerSample sample)
    {
        return frame.TryGetValue(role, out sample);
    }

    private Vector3 ConvertPosition(Vector3 vmcPosition)
    {
        // Slime VMC output inverts Z for Unity compatibility; invert back for app-world consistency.
        Vector3 converted = new Vector3(vmcPosition.x, vmcPosition.y, -vmcPosition.z);
        return _yawOffset * converted;
    }

    private Quaternion ConvertRotation(Quaternion vmcRotation)
    {
        // Slime VMC output inverts Z/W; undo that transform.
        Quaternion converted = new Quaternion(vmcRotation.x, vmcRotation.y, -vmcRotation.z, -vmcRotation.w);
        return _yawOffset * converted;
    }

    private void SetMappedBone(OVRSkeleton.BoneId boneId, Vector3 position, Quaternion rotation, bool smooth)
    {
        if (smooth)
        {
            if (!_smoothedPositions.TryGetValue(boneId, out Vector3 prevPos))
            {
                prevPos = position;
            }
            if (!_smoothedRotations.TryGetValue(boneId, out Quaternion prevRot))
            {
                prevRot = rotation;
            }

            position = Vector3.Lerp(prevPos, position, 1f - smoothingStrength);
            rotation = Quaternion.Slerp(prevRot, rotation, 1f - smoothingStrength);
            _smoothedPositions[boneId] = position;
            _smoothedRotations[boneId] = rotation;
        }

        _mappedBones[boneId] = new BodyPoseProvider.BoneData
        {
            id = boneId,
            position = position,
            rotation = rotation
        };
    }
}
