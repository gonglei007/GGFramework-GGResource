using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace GGFramework.GGResource.Tests
{
    /// <summary>
    /// ResourceSystem 的最小 EditMode 测试。
    /// 只覆盖不依赖 Init/Loxodon 运行时的“内存态缓存与查询面”，用于锁定既有语义与 P0 类型过滤修复。
    /// 异步加载分支（含本次 P1 修复）需真机/PlayMode 环境，不在此覆盖。
    /// </summary>
    public class ResourceSystemTests
    {
        private GameObject hostGo;
        private ResourceSystem sys;
        private readonly List<Object> createdAssets = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            hostGo = new GameObject("ResourceSystemTestHost");
            sys = hostGo.AddComponent<ResourceSystem>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object a in createdAssets)
            {
                if (a != null)
                {
                    Object.DestroyImmediate(a);
                }
            }
            createdAssets.Clear();

            if (hostGo != null)
            {
                Object.DestroyImmediate(hostGo);
            }

            // Singleton.OnDestroy 会把全局退出标记置 true，复位以免污染后续用例。
            SingletonLifetime.IsQuitting = false;
            sys = null;
        }

        private Texture2D NewTexture()
        {
            Texture2D t = new Texture2D(1, 1);
            createdAssets.Add(t);
            return t;
        }

        private GameObject NewGameObject()
        {
            GameObject g = new GameObject("asset");
            createdAssets.Add(g);
            return g;
        }

        [Test]
        public void GetKeyByGroupTag_FormatsGroupDashTag()
        {
            Assert.AreEqual("lobby-UI", sys.GetKeyByGroupTag("lobby", ResourceSystem.Tag.UI));
            Assert.AreEqual("all-Model", sys.GetKeyByGroupTag("all", ResourceSystem.Tag.Model));
        }

        [Test]
        public void GetResourceTable_CachesSameInstancePerGroupTag()
        {
            Dictionary<string, ResourceObject> t1 = sys.GetResourceTable("all", ResourceSystem.Tag.UI);
            Dictionary<string, ResourceObject> t2 = sys.GetResourceTable("all", ResourceSystem.Tag.UI);
            Dictionary<string, ResourceObject> t3 = sys.GetResourceTable("all", ResourceSystem.Tag.Model);

            Assert.IsNotNull(t1);
            Assert.AreSame(t1, t2, "同一 group-tag 应复用同一张表");
            Assert.AreNotSame(t1, t3, "不同 tag 应是不同的表");
        }

        [Test]
        public void SetObject_GetObject_Roundtrip()
        {
            ResourceObject ro = new ResourceObject("bundle1", "hero", NewTexture());
            sys.SetObject(sys.Group, ResourceSystem.Tag.UI, ro);

            ResourceObject got = sys.GetObject(ResourceSystem.Tag.UI, "hero");
            Assert.IsNotNull(got);
            Assert.AreSame(ro, got);
        }

        [Test]
        public void SetObject_Null_IsIgnored()
        {
            sys.SetObject(sys.Group, ResourceSystem.Tag.UI, null);
            Assert.IsNull(sys.GetObject(ResourceSystem.Tag.UI, "hero"));
        }

        [Test]
        public void GetObject_Unknown_ReturnsNull()
        {
            Assert.IsNull(sys.GetObject(ResourceSystem.Tag.UI, "missing"));
        }

        [Test]
        public void ListenObject_WhenAlreadyLoaded_InvokesImmediately()
        {
            ResourceObject ro = new ResourceObject("b", "panel", NewTexture());
            sys.SetObject(sys.Group, ResourceSystem.Tag.UI, ro);

            ResourceObject received = null;
            sys.ListenObject("panel", o => received = o);

            Assert.AreSame(ro, received, "已加载时监听应立即回调");
        }

        [Test]
        public void ListenObject_WhenNotLoaded_DefersUntilSetObject()
        {
            ResourceObject received = null;
            sys.ListenObject("deferred", o => received = o);
            Assert.IsNull(received, "未加载时不应立即回调");

            ResourceObject ro = new ResourceObject("b", "deferred", NewTexture());
            sys.SetObject(sys.Group, ResourceSystem.Tag.UI, ro);

            Assert.AreSame(ro, received, "加载完成后应触发挂起的监听");
        }

        [Test]
        public void GetAssets_UnknownBundle_ReturnsNull()
        {
            Assert.IsNull(sys.GetAssets<Texture2D>(ResourceSystem.Tag.Model, "no-such-bundle"));
        }

        [Test]
        public void GetAssets_FiltersByAssetType()
        {
            // 反射注入私有 assetsInBundle，构造一个含混合类型的 bundle 清单。
            FieldInfo field = typeof(ResourceSystem).GetField("assetsInBundle", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "未找到私有字段 assetsInBundle");
            Dictionary<string, List<string>> assetsInBundle = (Dictionary<string, List<string>>)field.GetValue(sys);
            assetsInBundle["mixed"] = new List<string> { "tex1", "go1", "tex2" };

            sys.SetObject(sys.Group, ResourceSystem.Tag.Model, new ResourceObject("mixed", "tex1", NewTexture()));
            sys.SetObject(sys.Group, ResourceSystem.Tag.Model, new ResourceObject("mixed", "go1", NewGameObject()));
            sys.SetObject(sys.Group, ResourceSystem.Tag.Model, new ResourceObject("mixed", "tex2", NewTexture()));

            // 锁定 P0 修复：按 `is T` 精确过滤，而非强制转换。
            ResourceObject[] textures = sys.GetAssets<Texture2D>(ResourceSystem.Tag.Model, "mixed");
            Assert.IsNotNull(textures);
            Assert.AreEqual(2, textures.Length, "应只返回 Texture2D 类型的资源");
            foreach (ResourceObject r in textures)
            {
                Assert.IsInstanceOf<Texture2D>(r.assetObject);
            }

            ResourceObject[] gameObjects = sys.GetAssets<GameObject>(ResourceSystem.Tag.Model, "mixed");
            Assert.IsNotNull(gameObjects);
            Assert.AreEqual(1, gameObjects.Length, "应只返回 GameObject 类型的资源");
            Assert.IsInstanceOf<GameObject>(gameObjects[0].assetObject);
        }
    }
}
