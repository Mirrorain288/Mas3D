using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 3 picture: the player's own legs folded for crouch (pat-me) and kneel (kiss), shot from a
    /// three-quarter angle so the knees and the floor line are both visible. Explicit, like
    /// PlayerFacingShotTests: run it alone with
    ///   unity.exe test apps\unity --mode PlayMode --filter Mas3D.Tests.PlayerLegsShotTests
    /// and it writes Shots/legs-crouch.png and Shots/legs-kneel.png.
    /// </summary>
    public class PlayerLegsShotTests
    {
        static IEnumerator Shoot(Camera camera, string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            if (File.Exists(file)) File.Delete(file);

            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = rt;
            for (var i = 0; i < 4; i++) yield return null;

            RenderTexture.active = rt;
            var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            texture.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(file, texture.EncodeToPNG());

            camera.targetTexture = null;
            Object.Destroy(texture);
            rt.Release();
        }

        static void Frame(Camera camera, Vector3 target)
        {
            camera.transform.position = target + new Vector3(1.45f, 0.55f, -1.55f);
            camera.transform.LookAt(target + new Vector3(0f, 0.10f, 0f));
            camera.fieldOfView = 42f;
        }

        /// <summary>Profile view from the player's own side, low, so knees and the sole line read.</summary>
        static void FrameSide(Camera camera, Transform player, Vector3 hips)
        {
            var aim = new Vector3(hips.x, 0.24f, hips.z);
            camera.transform.position = aim + player.right * 1.9f + Vector3.up * 0.30f;
            camera.transform.LookAt(aim);
            camera.fieldOfView = 36f;
        }

        [UnityTest]
        [Explicit]
        public IEnumerator TheFoldedLegsFromThreeQuarters()
        {
            AvatarPrefabFixture.Require();
            if (!PlayerBody.PrefabAvailable) Assert.Ignore("missing " + PlayerBody.BodyPrefabPath);

            var actor = AvatarActor.Spawn();

            var go = new GameObject("Player");
            go.AddComponent<CharacterController>();
            var player = go.AddComponent<PlayerMotor>();
            player.KeyboardInput = true;
            player.EnsureReady();

            var body = go.AddComponent<PlayerBody>();
            body.Attach(player, player.Head, null);
            Assert.IsTrue(body.Spawn(), "the body did not spawn");

            var dir = new GameObject("Actions").AddComponent<ActionDirector>();
            dir.Actor = actor;
            dir.Player = player;
            dir.KeyboardInput = false;
            dir.DriveInUpdate = false;

            var key = new GameObject("Key").AddComponent<Light>();
            key.type = LightType.Directional;
            key.intensity = 1.4f;
            key.transform.rotation = Quaternion.Euler(38f, -35f, 0f);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "ShotFloor";
            var floorRenderer = floor.GetComponent<Renderer>();
            var floorShader = Shader.Find("Universal Render Pipeline/Lit");
            if (floorShader != null)
            {
                var mat = new Material(floorShader);
                mat.color = new Color(0.30f, 0.31f, 0.33f);
                floorRenderer.sharedMaterial = mat;
            }
            floor.transform.position = Vector3.zero;

            var camGo = new GameObject("ShotCamera");
            var camera = camGo.AddComponent<Camera>();
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 50f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.16f, 0.17f, 0.20f);

            var shots = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            try
            {
                player.SetPosture(PlayerPosture.Crouching);
                Assert.IsTrue(dir.TryEnterAction(ActionId.PatMe), "pat-me refused: " + dir.LastFaults);
                ActionTestDrive.ToHold(dir);
                ActionTestDrive.Hold(dir, 0.4f);
                var hips = body.Avatar.Bone(HumanBodyBones.Hips).position;
                Frame(camera, hips);
                yield return Shoot(camera, Path.Combine(shots, "legs-crouch.png"));
                FrameSide(camera, player.transform, hips);
                yield return Shoot(camera, Path.Combine(shots, "legs-crouch-side.png"));

                dir.Cancel();
                player.SetPosture(PlayerPosture.Kneeling);
                Assert.IsTrue(dir.TryEnterAction(ActionId.Kiss), "kiss refused: " + dir.LastFaults);
                ActionTestDrive.ToHold(dir);
                ActionTestDrive.Hold(dir, 0.4f);
                hips = body.Avatar.Bone(HumanBodyBones.Hips).position;
                Frame(camera, hips);
                yield return Shoot(camera, Path.Combine(shots, "legs-kneel.png"));
                FrameSide(camera, player.transform, hips);
                yield return Shoot(camera, Path.Combine(shots, "legs-kneel-side.png"));
                dir.Cancel();
            }
            finally
            {
                Object.Destroy(camGo);
                Object.Destroy(floor);
                Object.Destroy(key.gameObject);
                Object.Destroy(dir.gameObject);
                Object.Destroy(actor.gameObject);
                Object.Destroy(go);
            }
        }
    }
}
