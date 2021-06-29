﻿using BepuUtilities.Collections;
using BepuPhysics;
using BepuPhysics.Constraints;
using System.Numerics;
using BepuUtilities;

namespace DemoRenderer.Constraints
{
    struct AngularSwivelHingeLineExtractor : IConstraintLineExtractor<AngularSwivelHingePrestepData>
    {
        public int LinesPerConstraint => 2;
        
        public unsafe void ExtractLines(ref AngularSwivelHingePrestepData prestepBundle, int setIndex, int* bodyIndices,
            Bodies bodies, ref Vector3 tint, ref QuickList<LineInstance> lines)
        {
            //Could do bundles of constraints at a time, but eh.
            ref var poseA = ref bodies.Sets[setIndex].MotionStates[bodyIndices[0]].Pose;
            ref var poseB = ref bodies.Sets[setIndex].MotionStates[bodyIndices[1]].Pose;
            Vector3Wide.ReadFirst(prestepBundle.LocalSwivelAxisA, out var localSwivelAxisA);
            Vector3Wide.ReadFirst(prestepBundle.LocalHingeAxisB, out var localHingeAxisB);
            QuaternionEx.Transform(localSwivelAxisA, poseA.Orientation, out var swivelAxis);
            QuaternionEx.Transform(localHingeAxisB, poseB.Orientation, out var hingeAxis);
            var color = new Vector3(0.2f, 0.7f, 1f) * tint;
            var packedColor = Helpers.PackColor(color);
            var backgroundColor = new Vector3(0f, 0f, 1f) * tint;
            lines.AllocateUnsafely() = new LineInstance(poseA.Position, poseA.Position + swivelAxis, packedColor, 0);
            lines.AllocateUnsafely() = new LineInstance(poseB.Position, poseB.Position + hingeAxis, packedColor, 0);
        }

    }
}
