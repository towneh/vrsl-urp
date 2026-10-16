#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Row L17's checks, shared by the DMX and AudioLink assemblies: a migrated fixture's body
    /// sits where the original's did and its light shines the way the original's beam did.
    /// The original's layout is stated as literals, since no test project has the original
    /// package.
    /// </summary>
    static class VRSLMigrationPlacement
    {
        /// <summary>Source poses to place from: turned about every axis, moved, and scaled
        /// uniformly and not.</summary>
        public static readonly (Vector3 position, Vector3 euler, Vector3 scale)[] SourcePoses =
        {
            (new Vector3(3f, 2f, -1f), new Vector3(20f, 130f, -35f), Vector3.one * 1.5f),
            (new Vector3(-2f, 4f, 5f), new Vector3(-60f, 15f, 80f), new Vector3(1f, 2f, 0.5f)),
        };

        public static VRSLMigrationPair Pair(string friendlyName)
        {
            var pair = VRSLMigrationTable.Pairs.FirstOrDefault(p => p.FriendlyName == friendlyName);
            Assert.AreEqual(friendlyName, pair.FriendlyName, $"the migration table has no pair named {friendlyName}");
            return pair;
        }

        public static GameObject UrpAsset(VRSLMigrationPair pair)
        {
            var path = AssetDatabase.GUIDToAssetPath(pair.UrpGuid);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(asset, $"{pair.FriendlyName}: no URP prefab at '{path}'");
            return asset;
        }

        public static void Pose(Transform source, (Vector3 position, Vector3 euler, Vector3 scale) pose)
        {
            source.SetPositionAndRotation(pose.position, Quaternion.Euler(pose.euler));
            source.localScale = pose.scale;
        }

        /// <summary>
        /// The original's par and blinder carry their body at the root, unrotated, at
        /// <paramref name="originalBodyScale"/>, so it sits on the source's own transform.
        /// </summary>
        public static void AssertBodyOnOriginal(GameObject sibling, Transform source, string bodyName,
            float originalBodyScale, string what)
        {
            var body = sibling.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == bodyName);
            Assert.IsNotNull(body, $"{what}: the URP prefab has no {bodyName}");

            var expected = source.localToWorldMatrix * Matrix4x4.Scale(Vector3.one * originalBodyScale);
            var actual = body.localToWorldMatrix;
            for (int i = 0; i < 16; i++)
                Assert.AreEqual(expected[i], actual[i], 1e-4f,
                    $"{what}: the body isn't where the original's was (matrix element {i}); "
                    + $"its up axis is {Vector3.Angle(source.up, body.up):F1}° from the original's, "
                    + $"its centre {Vector3.Distance(source.position, body.position):F3} m away");
        }

        /// <summary>The original's par and blinder shine down their root's −Y.</summary>
        public static void AssertShinesLikeOriginal(Vector3 worldDirection, Transform source, string what)
        {
            float angle = Vector3.Angle(source.TransformDirection(Vector3.down), worldDirection);
            Assert.Less(angle, 0.01f, $"{what}: the light shines {angle:F1}° from where the original's beam did");
        }
    }
}
#endif
