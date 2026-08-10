# GGResource 快速接入指南

> GGResource 是 GGFramework 的 Unity 资源加载系统，负责对 LoxodonFramework 的 Bundle 加载能力进行封装，
> 提供 **编辑器 Simulation 模拟加载** 与 **运行时 AssetBundle 加载** 双路径支持、资源缓存、加载队列、优先级加载与监听回调等能力。

- 适用 Unity 版本：**Unity 2021.3.4f1c1**
- 核心依赖：**LoxodonFramework**（Bundles / Contexts / Asynchronous / Security.Cryptography）、**CsAsync**（Whilst / Waterfall）、**SimpleJson**
- 核心单例：`ResourceSystem`（位于 `GGResource/Assets/Scripts/GGResource/Systems/ResourceSystem.cs`，继承 `Singleton<ResourceSystem>`）

---

## 一、将代码复制到你的工程

### 1. 拷贝项目目录

把以下目录、文件整体复制进目标工程（放入 `Assets/` 下任意位置均可，注意保持相对结构）：

```
Assets/
├── Scripts/
│   ├── GGResource/                       # GGResource 框架源码（核心）
│   │   ├── Systems/ResourceSystem.cs     # 资源系统单例
│   │   └── Resource/
│   │       ├── GBundleBuilder.cs         # 自定义加载器工厂
│   │       ├── GMappingPathInfoParser.cs # 路径<->Bundle 映射（运行时）
│   │       ├── GSimulationAutoMappingPathInfoParser.cs # 编辑器模拟映射
│   │       ├── ResourceItem.cs           # 加载任务描述
│   │       └── ResourceObject.cs         # 结果缓存对象
│   └── Dependency/Utils/Singleton.cs     # 泛型 MonoBehaviour 单例基类
├── LocalPackages/LoxodonFramework/       # LoxodonFramework 依赖（Bundles 等）
└── ...
```

> 依赖包（CsAsync、SimpleJson、LoxodonFramework）需一并就位。请确认工程中已引入
> `Loxodon.Framework.Bundles`、`Loxodon.Framework.Contexts`、`Loxodon.Framework.Security.Cryptography`、
> `Loxodon.Framework.Asynchronous`、`CsAsync`、`SimpleJson` 命名空间。

### 2. 添加一条 `using`

在脚本顶部引用即可（ResourceSystem 定义在全局命名空间中）：

```csharp
using UnityEngine;
// ResourceSystem / ResourceItem / ResourceObject 均在默认全局命名空间中
```

---

## 二、资源打包配置（AssetBundle）

运行时双路径中，资源通过 **AssetBundle + BundleManifest** 加载，需要进行 AB 打包。

### 1. 为资源设置 AssetBundle 名称

在 Unity 中选中资源，在 Inspector 设置：

- **AssetBundle 名**（如 `uipanels/prefabs`）
- 建议用**小写**名称（`ResourceSystem` 内部大量使用 `.ToLower()` 做归一化）

### 2. 生成 BundleManifest

`ResourceSystem.CreateResources()`（运行时分支）会在以下目录查找清单文件：

```
persistentDataPath + BundleSetting.ManifestFilename   // 可写目录（优先）
streamingAssetsPath + BundleSetting.ManifestFilename  // 只读目录（兜底）
```

构建出的 Bundle 文件需放入 **StreamingAssets 对应目录**（`streamingAssetsPath/bundles`）或
**Persistent 可写目录**（`persistentDataPath/bundles`）。

> `manifest.ActiveVariants = new string[] { "", "hd" };` 在源码中固定设定，即当前**激活变体为 `hd`**。

---

## 三、初始化 ResourceSystem

### 1. 创建单例

`ResourceSystem` 继承自 `Singleton<T>`（MonoBehaviour 单例）。首次访问 `ResourceSystem.Instance` 时会在场景中自动创建
名为 `(singleton) ResourceSystem` 的 GameObject，并挂上 `DontDestroyOnLoad`。

可直接通过 `ResourceSystem.Instance` 获取实例：

```csharp
// 获取并初始化单例
ResourceSystem rs = ResourceSystem.Instance;
rs.Init("myframework_bundle");   // 传入框架 Bundle 名
```

### 2. 编辑器 Simulation 模式

编辑器下可通过 `Loxodon.Framework.Bundles.SimulationSetting.IsSimulationMode` 在 **Simulation（直接加载 Asset）**
与 **运行时 AssetBundle** 两种模式间切换（即菜单：`Loxodon::Framework::Bundle::SimulateInEditor`）：

```csharp
#if UNITY_EDITOR
Loxodon.Framework.Bundles.SimulationSetting.IsSimulationMode = true; // 打开模拟模式
#endif
```

> Simulation 模式不读取真实 Bundle，而是通过 `GSimulationAutoMappingPathInfoParser` 依据 AssetBundle 名称自动映射
> 到 `AssetDatabase` 中的资源，便于编辑器内快速开发调试。`IsSimulationMode` 的 setter 实现是取反，
> 需注意调用行为。

---

## 四、最小示例：按 Tag / Group 加载资源

### 方式 A：通过 `AddResourceItem` + `StartLoading` 批量按优先级加载

`ResourceItem` 构造参数：`(string name, ResourceSystem.Tag tag, string path, int priority)`

- `priority = 0` → 初始（高优先级）资源，会被加入 `p0ResourceSet`，全部完成后触发 `onHighResourceLoaded`
- `priority = -1` → 不加载
- `priority > 0` → 按数值升序入队加载

```csharp
using UnityEngine;

public class ResourceDemo : MonoBehaviour
{
    ResourceSystem rs;

    void Start()
    {
        rs = ResourceSystem.Instance;
        rs.Init("framework");

        rs.Group = "all";   // 组名（源码中 setter 强制归一化为 "all"）

        // 1. 加入要加载的资源
        rs.AddResourceItem(new ResourceItem("UI_MainPanel", ResourceSystem.Tag.UI,   "uipanels/prefabs/mainpanel",  0));
        rs.AddResourceItem(new ResourceItem("Hero_Model",   ResourceSystem.Tag.Model,"characters/hero",             5));

        // 2. 注册回调并开始加载
        rs.StartLoading(
            (Exception e) => { Debug.Log("高优先级(priority==0)资源加载完成"); },
            (Exception e) => { Debug.Log("全部资源加载完成"); }
        );

        // 3.（可选）监听单个对象的加载完成事件
        rs.onResourceLoaded += (ResourceItem item) =>
            Debug.LogFormat("对象已就绪: name={0} path={1} tag={2}", item.name, item.path, item.tag);
    }

    void Update()
    {
        if (rs != null && rs.IsAllResourceLoaded())
        {
            // 全部加载完成，取用缓存
            ResourceObject panel = rs.GetObject(ResourceSystem.Tag.UI, "UI_MainPanel");
            if (panel != null)
                GameObject.Instantiate(panel.assetObject);
        }
    }
}
```

### 方式 B：按 Tag/Group 构建过滤表

资源按 `Group`（组）+ `Tag`（分类）组织，形成一张张资源表：

```csharp
// 取"组 all / Tag UI"的资源表
System.Collections.Generic.Dictionary<string, ResourceObject> table =
    rs.GetResourceTable(rs.Group, ResourceSystem.Tag.UI);

foreach (var kv in table)
    Debug.LogFormat("资源名: {0}, Bundle名: {1}", kv.Key, kv.Value.bundleName);
```

### 方式 C：异步加载单个 float 资源

用于“随时、独立打成 bundle”的资源。若该资源是 `GameObject` 类型，加载完成后会自动 `Instantiate`：

```csharp
// 单参数重载：bundle 名 / asset 名取同名
rs.LoadFloatAssetAsync("UIFloat_Tips", (UnityEngine.Object obj) =>
{
    if (obj == null) return;
    // obj 可能是实例化的 GameObject 或原生资源
});
```

---

## 五、下一步

更多进阶用法（加载来源顺序、Tag/Group 分类体系、优先级机制、监听 `ListenObject`、加密 Bundle、WebGL 差异、
完整 API 参考）请参阅 **[manual.md](./manual.md)**。
