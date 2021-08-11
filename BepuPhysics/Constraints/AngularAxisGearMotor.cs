﻿using BepuPhysics.CollisionDetection;
using BepuUtilities;
using BepuUtilities.Memory;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using static BepuUtilities.GatherScatter;

namespace BepuPhysics.Constraints
{
    /// <summary>
    /// Constrains body B's angular velocity around an axis anchored to body A to equal body A's velocity around that axis with a scaling factor applied.
    /// </summary>
    public struct AngularAxisGearMotor : ITwoBodyConstraintDescription<AngularAxisGearMotor>
    {
        /// <summary>
        /// Axis of rotation in body A's local space.
        /// </summary>
        public Vector3 LocalAxisA;
        /// <summary>
        /// <para>Scale to apply to body A's velocity around the axis to get body B's target velocity.</para>
        /// <para>In other words, a VelocityScale of 2 means that body A could have a velocity of 3 while body B has a velocity of 6.</para>
        /// </summary>
        public float VelocityScale;
        /// <summary>
        /// Motor control parameters.
        /// </summary>
        public MotorSettings Settings;

        public readonly int ConstraintTypeId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return AngularAxisGearMotorTypeProcessor.BatchTypeId;
            }
        }

        public readonly Type TypeProcessorType => typeof(AngularAxisGearMotorTypeProcessor);

        public readonly void ApplyDescription(ref TypeBatch batch, int bundleIndex, int innerIndex)
        {
            ConstraintChecker.AssertUnitLength(LocalAxisA, nameof(AngularAxisGearMotor), nameof(LocalAxisA));
            ConstraintChecker.AssertValid(Settings, nameof(AngularAxisGearMotor));
            Debug.Assert(ConstraintTypeId == batch.TypeId, "The type batch passed to the description must match the description's expected type.");
            ref var target = ref GetOffsetInstance(ref Buffer<AngularAxisGearMotorPrestepData>.Get(ref batch.PrestepData, bundleIndex), innerIndex);
            Vector3Wide.WriteFirst(LocalAxisA, ref target.LocalAxisA);
            GetFirst(ref target.VelocityScale) = VelocityScale;
            MotorSettingsWide.WriteFirst(Settings, ref target.Settings);
        }

        public readonly void BuildDescription(ref TypeBatch batch, int bundleIndex, int innerIndex, out AngularAxisGearMotor description)
        {
            Debug.Assert(ConstraintTypeId == batch.TypeId, "The type batch passed to the description must match the description's expected type.");
            ref var source = ref GetOffsetInstance(ref Buffer<AngularAxisGearMotorPrestepData>.Get(ref batch.PrestepData, bundleIndex), innerIndex);
            Vector3Wide.ReadFirst(source.LocalAxisA, out description.LocalAxisA);
            description.VelocityScale = GetFirst(ref source.VelocityScale);
            MotorSettingsWide.ReadFirst(source.Settings, out description.Settings);
        }
    }

    public struct AngularAxisGearMotorPrestepData
    {
        public Vector3Wide LocalAxisA;
        public Vector<float> VelocityScale;
        public MotorSettingsWide Settings;
    }

    public struct AngularAxisGearMotorProjection
    {
        public Vector3Wide NegatedVelocityToImpulseB;
        public Vector<float> VelocityScale;
        public Vector<float> SoftnessImpulseScale;
        public Vector<float> MaximumImpulse;
        public Vector3Wide ImpulseToVelocityA;
        public Vector3Wide NegatedImpulseToVelocityB;
    }


    public struct AngularAxisGearMotorFunctions : ITwoBodyConstraintFunctions<AngularAxisGearMotorPrestepData, AngularAxisGearMotorProjection, Vector<float>>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Prestep(in QuaternionWide orientationA, in BodyInertiaWide inertiaA, in Vector3Wide ab, in QuaternionWide orientationB, in BodyInertiaWide inertiaB,
            float dt, float inverseDt, ref AngularAxisGearMotorPrestepData prestep, out AngularAxisGearMotorProjection projection)
        {
            //Velocity level constraint that acts directly on the given axes. Jacobians just the axes, nothing complicated. 1DOF, so we do premultiplication.
            //This is mildly more complex than the AngularAxisMotor:
            //dot(wa, axis) - dot(wb, axis) * velocityScale = 0, so jacobianB is actually -axis * velocityScale, not just -axis.
            QuaternionWide.TransformWithoutOverlap(prestep.LocalAxisA, orientationA, out var axis);
            Vector3Wide.Scale(axis, prestep.VelocityScale, out var jA);
            Symmetric3x3Wide.TransformWithoutOverlap(jA, inertiaA.InverseInertiaTensor, out projection.ImpulseToVelocityA);
            Vector3Wide.Dot(jA, projection.ImpulseToVelocityA, out var contributionA);
            Symmetric3x3Wide.TransformWithoutOverlap(axis, inertiaB.InverseInertiaTensor, out projection.NegatedImpulseToVelocityB);
            Vector3Wide.Dot(axis, projection.NegatedImpulseToVelocityB, out var contributionB);
            MotorSettingsWide.ComputeSoftness(prestep.Settings, dt, out var effectiveMassCFMScale, out projection.SoftnessImpulseScale, out projection.MaximumImpulse);
            var effectiveMass = effectiveMassCFMScale / (contributionA + contributionB);

            Vector3Wide.Scale(axis, effectiveMass, out projection.NegatedVelocityToImpulseB);
            projection.VelocityScale = prestep.VelocityScale;

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ApplyImpulse(ref Vector3Wide angularVelocityA, ref Vector3Wide angularVelocityB, in AngularAxisGearMotorProjection projection, in Vector<float> csi)
        {
            Vector3Wide.Scale(projection.ImpulseToVelocityA, csi, out var velocityChangeA);
            Vector3Wide.Scale(projection.NegatedImpulseToVelocityB, csi, out var negatedVelocityChangeB);
            Vector3Wide.Add(angularVelocityA, velocityChangeA, out angularVelocityA);
            Vector3Wide.Subtract(angularVelocityB, negatedVelocityChangeB, out angularVelocityB);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WarmStart(ref BodyVelocityWide velocityA, ref BodyVelocityWide velocityB, ref AngularAxisGearMotorProjection projection, ref Vector<float> accumulatedImpulse)
        {
            ApplyImpulse(ref velocityA.Angular, ref velocityB.Angular, projection, accumulatedImpulse);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Solve(ref BodyVelocityWide velocityA, ref BodyVelocityWide velocityB, ref AngularAxisGearMotorProjection projection, ref Vector<float> accumulatedImpulse)
        {
            //csi = projection.BiasImpulse - accumulatedImpulse * projection.SoftnessImpulseScale - (csiaLinear + csiaAngular + csibLinear + csibAngular);
            Vector3Wide.Dot(velocityA.Angular, projection.NegatedVelocityToImpulseB, out var unscaledCSIA);
            Vector3Wide.Dot(velocityB.Angular, projection.NegatedVelocityToImpulseB, out var negatedCSIB);
            var csi = -accumulatedImpulse * projection.SoftnessImpulseScale - (unscaledCSIA * projection.VelocityScale - negatedCSIB);
            ServoSettingsWide.ClampImpulse(projection.MaximumImpulse, ref accumulatedImpulse, ref csi);
            ApplyImpulse(ref velocityA.Angular, ref velocityB.Angular, projection, csi);

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ApplyImpulse(in Vector3Wide jA, in Vector3Wide negatedJB, in Vector<float> csi, in Symmetric3x3Wide inertiaA, in Symmetric3x3Wide inertiaB, ref Vector3Wide angularVelocityA, ref Vector3Wide angularVelocityB)
        {
            Symmetric3x3Wide.TransformWithoutOverlap(jA * csi, inertiaA, out var changeA);
            angularVelocityA += changeA;
            Symmetric3x3Wide.TransformWithoutOverlap(negatedJB * csi, inertiaB, out var negatedChangeB);
            angularVelocityA -= negatedChangeB;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WarmStart2(in QuaternionWide orientationA, in BodyInertiaWide inertiaA, in Vector3Wide ab, in QuaternionWide orientationB, in BodyInertiaWide inertiaB, in AngularAxisGearMotorPrestepData prestep, in Vector<float> accumulatedImpulses, ref BodyVelocityWide wsvA, ref BodyVelocityWide wsvB)
        {
            QuaternionWide.TransformWithoutOverlap(prestep.LocalAxisA, orientationA, out var axis);
            ApplyImpulse(axis * prestep.VelocityScale, axis, accumulatedImpulses, inertiaA.InverseInertiaTensor, inertiaB.InverseInertiaTensor, ref wsvA.Angular, ref wsvB.Angular);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Solve2(in QuaternionWide orientationA, in BodyInertiaWide inertiaA, in Vector3Wide ab, in QuaternionWide orientationB, in BodyInertiaWide inertiaB, float dt, float inverseDt, in AngularAxisGearMotorPrestepData prestep, ref Vector<float> accumulatedImpulses, ref BodyVelocityWide wsvA, ref BodyVelocityWide wsvB)
        {
            QuaternionWide.TransformWithoutOverlap(prestep.LocalAxisA, orientationA, out var axis);
            Vector3Wide.Scale(axis, prestep.VelocityScale, out var jA);
            Symmetric3x3Wide.TransformWithoutOverlap(jA, inertiaA.InverseInertiaTensor, out var jIA);
            Vector3Wide.Dot(jA, jIA, out var contributionA);
            Symmetric3x3Wide.TransformWithoutOverlap(axis, inertiaB.InverseInertiaTensor, out var jIB);
            Vector3Wide.Dot(axis, jIB, out var contributionB);
            MotorSettingsWide.ComputeSoftness(prestep.Settings, dt, out var effectiveMassCFMScale, out var softnessImpulseScale, out var maximumImpulse);

            //csi = projection.BiasImpulse - accumulatedImpulse * softnessImpulseScale - (csiaLinear + csiaAngular + csibLinear + csibAngular);
            var csi = effectiveMassCFMScale * (Vector3Wide.Dot(wsvB.Angular, axis) - Vector3Wide.Dot(wsvA.Angular, axis)) / (contributionA + contributionB) - accumulatedImpulses * softnessImpulseScale;
            ServoSettingsWide.ClampImpulse(maximumImpulse, ref accumulatedImpulses, ref csi);
            ApplyImpulse(jA, axis, accumulatedImpulses, inertiaA.InverseInertiaTensor, inertiaB.InverseInertiaTensor, ref wsvA.Angular, ref wsvB.Angular);
        }
    }

    public class AngularAxisGearMotorTypeProcessor : TwoBodyTypeProcessor<AngularAxisGearMotorPrestepData, AngularAxisGearMotorProjection, Vector<float>, AngularAxisGearMotorFunctions, AccessOnlyAngular, AccessOnlyAngular, AccessOnlyAngular, AccessOnlyAngular>
    {
        public const int BatchTypeId = 54;
    }
}

