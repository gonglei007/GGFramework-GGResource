using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GGFramework.GGResource.Tests
{
    // Native-object lifetime only. Does not initialize Loxodon or prove GPU/Bundle cleanup.
    public class ResourceReleasePlayModeTests
    {
        private GameObject host;
        private ResourceSystem system;
        private readonly List<Object> assets = new List<Object>();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            SingletonLifetime.IsQuitting = false;
            host = new GameObject("Resource release CI owner");
            system = host.AddComponent<ResourceSystem>();
            system.Group = "ci-exclusive-owner";
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (system != null) system.ClearObjects(system.Group);
            foreach (var asset in assets) if (asset != null) Object.Destroy(asset);
            assets.Clear();
            if (host != null) Object.Destroy(host);
            yield return null;
            yield return null;
            SingletonLifetime.IsQuitting = false;
        }

        [UnityTest]
        public IEnumerator LastReleaseDestroysNativeAssetAfterOtherOwnerReleases()
        {
            var asset = new TextAsset("runtime-owned");
            assets.Add(asset);
            var resource = new ResourceObject("ci", "shared", asset);
            system.SetObject(system.Group, ResourceSystem.Tag.Other, resource);
            system.Retain(ResourceSystem.Tag.Other, "shared");
            system.Retain(ResourceSystem.Tag.Other, "shared");
            system.DestroyObject(ResourceSystem.Tag.Other, "shared");
            yield return null;
            Assert.That(resource.refCount, Is.EqualTo(1));
            Assert.That(asset != null, Is.True, "An active owner must keep the native object alive");
            system.DestroyObject(ResourceSystem.Tag.Other, "shared");
            yield return null;
            yield return null;
            Assert.That(system.GetObject(ResourceSystem.Tag.Other, "shared"), Is.Null);
            Assert.That(asset == null, Is.True, "The final release must destroy the owned native object");
        }

        [UnityTest]
        public IEnumerator ExplicitOwnerTeardownClearsCacheAndNativeObjects()
        {
            var first = new GameObject("owned-first");
            var second = new TextAsset("owned-second");
            assets.Add(first);
            assets.Add(second);
            system.SetObject(system.Group, ResourceSystem.Tag.Model, new ResourceObject("ci", "first", first));
            system.SetObject(system.Group, ResourceSystem.Tag.Other, new ResourceObject("ci", "second", second));
            system.Retain(ResourceSystem.Tag.Model, "first");
            system.Retain(ResourceSystem.Tag.Other, "second");
            system.Retain(ResourceSystem.Tag.Other, "second");
            system.ClearObjects(system.Group);
            yield return null;
            yield return null;
            Assert.That(system.GetObject(ResourceSystem.Tag.Model, "first"), Is.Null);
            Assert.That(system.GetObject(ResourceSystem.Tag.Other, "second"), Is.Null);
            Assert.That(first == null && second == null, Is.True);
        }
    }
}
