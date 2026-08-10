# GGResource 使用手册

GGResource 是 GGFramework 的核心资源加载系统。它封装了 **LoxodonFramework 的 Bundle** 加载能力，
对外提供统一、异步、带缓存、可监听、支持优先级和分组的资源访问接口。

- 版本：Unity 2021.3.4f1c1
- 核心源码：`GGResource/Assets/Scripts/GGResource/`
- 核心单例：`ResourceSystem`（`Systems/ResourceSystem.cs`）
- 依赖：LoxodonFramework（Bundles / Contexts / Asynchronous / Security.Cryptography）、CsAsync、SimpleJson

---

## 目录

1. [体系结构](#1-体系结构)
2. [双路径：编辑器 Simulation / 运行时 AssetBundle](#2-双路径编辑器-simulation--运行时-assetbundle)
3. [多来源加载顺序](#3-多来源加载顺序)
4. [Tag / Group 分类体系](#4-tag--group-分类体系)
5. [优先级加载机制](#5-优先级加载机制)
6. [加载回调事件](#6-加载回调事件)
7. [资源缓存](#7-资源缓存)
8. [监听 ListenObject](#8-监听-listenobject)
9. [加密 Bundle 配置](#9-加密-bundle-配置)
10. [WebGL 差异](#10-webgl-差异)
11. [API 参考](#11-api-参考)

---

## 1. 体系结构

```
┌────────────────────────────────────────────────────────────┐
│                      调用方（游戏逻辑）                       │
└────────────────────────────┬───────────────────────────────┘
                             │ ResourceSystem.Instance
┌────────────────────────────▼───────────────────────────────┐
│                    ResourceSystem (单例)                     │
│  · 加载队列 / 优先级排序  · 缓存缓冲池 resourceBuffer        │
│  · 监听表 loadListeners   · 进度 LoadNumber / LoadTotal      │
└────────────────────────────┬───────────────────────────────┘
              ┌──────────────┴──────────────┐
              │ UNITY_EDITOR                 │
              │ SimulationSetting.IsSimulationMode
┌─────────────▼────────────┐   ┌─────────────▼────────────────┐
│   Simulation 路径         │   │   运行时 Bundle 路径          │
│  GSimulationAutoMapping   │   │   BundleManifestLoader       │
│  PathInfoParser           │   │   GMappingPathInfoParser     │
│  SimulationBundleManager  │   │   GBundleBuilder             │
│  SimulationResources      │   │   BundleManager              │
│  直接读 AssetDatabase     │   │   BundleResources            │
└───────────────────────────┘   └─────────────┬────────────────┘
       缓存 → Persistent → StreamingAssets → URL（GBundleBuilder 来源链）
```

- 每个 `ResourceItem` 是一个加载任务；`ResourceObject` 是加载结果缓存。
- 加载完成后 `ResourceSystem` 把 `ResourceObject` 写入按 `Group + Tag` 索引的 `resourceBuffer` 字典。

---

## 2. 双路径：编辑器 Simulation / 运行时 AssetBundle

`ResourceSystem.CreateResources()` 依据编译宏与 `SimulationSetting.IsSimulationMode` 决定创建哪种资源实现：

### 2.1 编辑器 Simulation 路径（`UNITY_EDITOR` 且 Simulation 模式开启）

```csharp
if (SimulationSetting.IsSimulationMode)
{
    IPathInfoParser pathInfoParser = new GSimulationAutoMappingPathInfoParser();
    IBundleManager manager = new SimulationBundleManager();
    resources = new SimulationResources(pathInfoParser, manager);
}
```

- 通过 `GSimulationAutoMappingPathInfoParser` 自动扫描 `AssetDatabaseHelper.GetUsedAssetBundleNames()`，
  把 **资产的 AssetBundle 名 → 小写资源路径** 建立映射（仅处理 `#if UNITY_EDITOR` 作用域）。
- **不读取真实 Bundle 文件**，直接加载工程内资源，便于编辑器内快速迭代。
- `ResourceSystem.LoadBundleAssetsAsync` 在模拟分支内部走 `AssetDatabase.GetAssetPathsFromAssetBundle...` + `resources.LoadAssetsAsync`。

### 2.2 运行时 AssetBundle 路径（真机 / 编辑器非模拟模式）

```csharp
IBundleManifestLoader manifestLoader = new BundleManifestLoader();
string manifestFilePath = BundleUtil.GetStorableDirectory() + BundleSetting.ManifestFilename;
if (!File.Exists(manifestFilePath))
    manifestFilePath = BundleUtil.GetReadOnlyDirectory() + BundleSetting.ManifestFilename;

BundleManifest manifest = manifestLoader.Load(manifestFilePath);
manifest.ActiveVariants = new string[] { "", "hd" };

IPathInfoParser pathInfoParser = new GMappingPathInfoParser(manifest);
IBundleManager manager = new BundleManager(manifest,
    new GBundleBuilder(new System.Uri(BundleUtil.GetReadOnlyDirectory()), false, null));
resources = new BundleResources(pathInfoParser, manager);
```

- 通过 `GMappingPathInfoParser`（实现自 `IPathInfoParser, IManifestUpdatable`）建立 **资源路径 → Bundle 名** 的映射字典。
- `manifest.ActiveVariants = { "", "hd" }`：激活 `hd` 变体。
- 每次创建后都会调用 `GenerateAssetBundleMap()`，把 `c:/Prefab/目录树` 下的资源名 → Bundle 名写入 `assetBundleMap`（用于按 Asset 名重排下载顺序）。

> **注意**：`FixShaders` 会在 `WindowsEditor / OSXEditor` 下对已加载 Bundle 内的材质做 shader 刷新，防止编辑器 shader 异常。

---

## 3. 多来源加载顺序

实际加载来源由 `GBundleBuilder.Create()` 决定，顺序（优先级从高到低）：

| 顺序 | 来源                          | 条件                                | 说明 |
|------|-------------------------------|-------------------------------------|------|
| 1    | **Unity 缓存（cache）**        | `useCache == true` 且 `BundleUtil.ExistsInCache(bundleInfo)` | 返回 `UnityWebRequestBundleLoader`（UNITY_5_4_OR_NEWER）|
| 2    | **可写目录（Persistent）**     | `BundleUtil.ExistsInStorableDirectory(bundleInfo)` | 路径：`Application.persistentDataPath/bundles` |
| 3    | **只读目录（StreamingAssets）** | `BundleUtil.ExistsInReadOnlyDirectory(bundleInfo)`，且非 WebGL（WebGL 走 `#if !UNITY_WEBGL || UNITY_EDITOR`） | 路径：`Application.streamingAssetsPath/bundles` |
| 4    | **网络 URL（远端）**           | 本地都不存在时降级，由 `IsRemoteUri(loadBaseUri)` 判断 | 返回 `UnityWebRequestBundleLoader`（远端）或 `FileAsyncBundleLoader`（本地文件） |

> 核心链路即源码注释：**Persistent → StreamingAssets → URL**。
> 若当前来源所定位的 Bundle 命中 `bundleInfo.IsEncrypted`，则改用 `CryptographBundleLoader` 并使用传入的 `decryptor` 解密（见第 9 节）。
> Manifest 查找同样遵循「先 Persistent 后 StreamingAssets」的顺序（见第 2.2 节）。

---

## 4. Tag / Group 分类体系

### 4.1 Tag（资源类型分类）

`ResourceSystem.Tag` 枚举（定义于 `ResourceSystem.cs`）：

```csharp
public enum Tag {
    UI,      // UI 资源
    Model,   // 模型资源
    VFX,     // 特效资源
    Other,   // 其他
}
```

### 4.2 Group（资源组）

`ResourceSystem.Group` 属性代表资源所属组。**源码中 setter 被强制写死为 `"all"`**（TODO：将来实现分组释放后再放开分组管理）：

```csharp
public string Group {
    get { return group; }
    set { group = "all"; }   // 当前固定 all，统一管理
}
```

因此当前所有资源都归入 `"all"` 组，`GetKeyByGroupTag` 生成形如 `"all-UI"` 的缓冲桶键。

```csharp
public string GetKeyByGroupTag(string group, Tag tag) {
    return group + "-" + tag.ToString();
}
```

---

## 5. 优先级加载机制

`ResourceItem.priority` 字段控制加载次序与“高优先级资源”的判定：

```csharp
public int priority;  // 0: 初始资源；-1: 不加载；>0: 加载顺序。
```

- **`priority == 0`**：加入 `p0ResourceSet`，视为“初始/高优先级资源”，等待期间 `IsHighResourceLoaded()` 返回 false。
- **`priority == -1`**：`FindLoadingResource` 返回 false，**不会被加入加载队列**（不加载）。
- **`priority > 0`**：按数值**升序**排列后依次加载（`CompareResourcePriority` 按 `x.priority - y.priority` 排序）。

### 加载流程

1. `AddResourceItem(ResourceItem)` 把任务加入 `resourceList`，`priority == 0` 的加入 `p0ResourceSet`。
2. `StartLoading(onHigh, onAll)`：
   - 通过 `FindLoadingResource` 筛选 `priority >= 0` 的项；
   - 通过 `CompareResourcePriority` 升序排序；
   - 把排序结果填入 `loadingList`，调用 `DoLoading()`。
3. `DoLoading()`：
   - 重置进度 `loadNumber = 0`，`loadTotal = p0ResourceSet.Count`；
   - 用 CsAsync 的 `Whilst` 顺序处理 `loadingList`，每次取队首 `loadingList[0]` 并移除；
   - 调用 `LoadFromBundleAsync(...)` 真正加载；
   - 每一步完成后 `loadNumber++`；
   - 当 `p0ResourceSet` 清空 → `isHighResourceLoaded = true` 并回调 `onHighResourceLoaded(null)`；
   - 当 `resourceCount <= 0`（全部完成）→ `isAllResourceLoaded = true` 并回调 `onAllResourceLoaded(null)`。

### 重排下载顺序

针对即将用到的资源可把它提到队列最前，接口基于 `assetBundleMap`（Prefab 名→Bundle 名，由 `GenerateAssetBundleMap` 生成）：

```csharp
rs.ReorderDownloadListByAssetName("UI_MainPanel");   // 按资源名（Prefab）找 Bundle 并重排
rs.ReorderDownloadListByBundleName("uipanels");      // 直接按 Bundle 名重排
```

---

## 6. 加载回调事件

`ResourceSystem` 提供三个公共回调字段（均为可直接赋值的 `public` 字段）：

| 回调字段                          | 签名                              | 触发时机 |
|-----------------------------------|-----------------------------------|----------|
| `onHighResourceLoaded`            | `Action<Exception>`               | 全部 `p0ResourceSet`（priority==0）加载完成 |
| `onAllResourceLoaded`             | `Action<Exception>`               | 加载队列全部处理完毕 |
| `onResourceLoaded`                | `Action<ResourceItem>`            | 单个 `ResourceItem` 的 asset 加载完成（`LoadFromBundleAsync` 内部） |

示例：

```csharp
rs.onHighResourceLoaded = (Exception e) => { /* 高优先级资源就绪 */ };
rs.onAllResourceLoaded  = (Exception e) => { /* 所有资源就绪，可进入游戏 */ };
rs.onResourceLoaded     = (ResourceItem item) =>
    Debug.Log($"loaded: {item.name} @ {item.path} tag={item.tag}");
```

> `StartLoading(onHigh, onAll)` 也会把传入的两个回调赋给以上字段，等价于手动赋值后再调用 `DoLoading()`。

---

## 7. 资源缓存

- 缓存容器：`resourceBuffer`（`Dictionary<string /*group-tag*/, Dictionary<string /*assetName*/, ResourceObject>>`）。
- 写入：`SetObject(group, Tag, ResourceObject)` → 通过 `GetResourceTable` 获取对应表，`resourceBuffer[key][assetName] = obj`，随后触发 `OnObjectLoaded`。
- 读取：`GetObject(Tag, name)`，取 `GetResourceTable(this.Group, tag)` 中缓存；未命中返回 `null`。
- 按 Bundle 过滤：`GetAssets<T>(Tag, bundleName)` 返回该 Bundle 下、类型匹配 `T` 的资源数组。
- 释放：`DestroyObject(Tag, name)` 会对 `obj.assetObject` 执行 `Destroy` 并从表移除；`ClearObjects(group)` / `ClearResources()` 当前为占位（待实现释放策略）。

```csharp
ResourceObject cached = rs.GetObject(ResourceSystem.Tag.UI, "UI_MainPanel");
if (cached != null) GameObject.Instantiate(cached.assetObject);
```

> `ResourceObject` 字段：`bundleName`、`assetName`、`assetObject`。

---

## 8. 监听 ListenObject

用于“资源可能尚未加载，但希望就绪时收到通知”的场景。

```csharp
public void ListenObject(string name, Action<ResourceObject> callback)
```

行为：

1. 先查 `GetObject(Tag.UI, name)`（注意：监听默认查 `Tag.UI` 组）。
2. 若已缓存 → **立即回调** `callback(resObject)`。
3. 若未缓存 → 把 `callback` 存入 `loadListeners[name]`；
   当该对象稍后通过 `SetObject` 写入缓存时，`OnObjectLoaded(name, obj)` 会取出并调用该回调。

```csharp
rs.ListenObject("UI_MainPanel", (ResourceObject ro) =>
{
    if (ro != null) GameObject.Instantiate(ro.assetObject);
});
```

---

## 9. 加密 Bundle 配置

`GBundleBuilder` 构造函数支持传入解密器 `IStreamDecryptor`（来自 `Loxodon.Framework.Security.Cryptography`）：

```csharp
public GBundleBuilder(Uri baseUri, bool useCache)
public GBundleBuilder(Uri baseUri, bool useCache, IStreamDecryptor decryptor)
```

- `useCache`：是否优先使用 Unity 缓存（见第 3 节）。
- `decryptor`：解密器。当 `bundleInfo.IsEncrypted` 且 `bundleInfo.Encoding == decryptor.AlgorithmName` 时，返回
  `CryptographBundleLoader` 进行解密加载；否则抛出 `NotSupportedException`（提示不支持的加密算法）。

> 当前 `ResourceSystem` 运行时分支创建的是 `new GBundleBuilder(baseUri, false, null)`，即**默认不加密**。
> 如需加密，请自行构造解密器（例如 `CryptographUtil.GetDecryptor(Algorithm.AES128_CBC_PKCS7, key, iv)`，该用法在源码中
> 以注释形式保留）并替换为 `new GBundleBuilder(new System.Uri(BundleUtil.GetReadOnlyDirectory()), useCache, decryptor)`。

---

## 10. WebGL 差异

- 在 `GBundleBuilder.Create()` 中，**StreamingAssets（只读目录）来源**被限定在
  `#if !UNITY_WEBGL || UNITY_EDITOR` 条件下（WebGL 下不尝试从 `Application.streamingAssetsPath` 直接读文件）。
- 因此 WebGL 平台主要依赖：**Unity 缓存 → Persistent 可写目录 → 网络 URL** 的链路。
- 其余逻辑（加密、变体 hd、Simulation 编辑器模式等）与其它平台一致。
- Unity 版本为 2021.3，`UNITY_5_4_OR_NEWER` 默认打开，因此网络/缓存加载走 `UnityWebRequestBundleLoader`
  （基于 UnityWebRequest，而非旧的 WWW）。

---

## 11. API 参考

> 以下所有方法/属性签名均核对自 `ResourceSystem.cs` 源码。

### 11.1 枚举

| 枚举 | 取值 | 说明 |
|------|------|------|
| `ResourceSystem.Tag` | `UI`, `Model`, `VFX`, `Other` | 资源类型分类 |
| `ResourceSystem.State` | `Unloaded`, `Loading`, `Loaded`, `Failed` | 状态（当前源码中为预留，未广泛使用） |

### 11.2 属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `Singleton<ResourceSystem>.Instance` | `ResourceSystem` | 全局单例；首次访问自动创建 `(singleton) ResourceSystem` GameObject（`DontDestroyOnLoad`）|
| `Group` | `string`（get/set） | 资源组；setter 当前被强制写为 `"all"` |
| `LoadNumber` | `int`（只读） | 当前已完成的加载数量（`DoLoading` 中自增）|
| `LoadTotal` | `int`（只读） | 总加载数量（= `p0ResourceSet.Count`）|

### 11.3 公共回调字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `onHighResourceLoaded` | `Action<Exception>` | 高优先级（priority==0）资源全部加载完成 |
| `onAllResourceLoaded` | `Action<Exception>` | 加载队列全部完成 |
| `onResourceLoaded` | `Action<ResourceItem>` | 单个资源加载完成的回调 |

### 11.4 方法

| 方法签名 | 说明 |
|----------|------|
| `void Init(string FrameworkBundleName)` | 初始化资源系统，创建 `IResources` 并注册到应用上下文容器 |
| `bool Initialized()` | 返回是否已初始化（当前恒为 true 的桩实现）|
| `void AddResourceItem(ResourceItem resourceItem)` | 加入一个加载任务；`priority==0` 时一并加入 `p0ResourceSet` |
| `void StartLoading(Action<Exception> onHighResourceLoaded, Action<Exception> onAllResourceLoaded)` | 筛选 `priority>=0` 项、按优先级升序排序后开始加载 |
| `void DoLoading()` | 执行加载队列（CsAsync `Whilst` 顺序处理）|
| `void Reset()` | 重置加载标记与队列（`isHighResourceLoaded` / `isAllResourceLoaded` 置 false，清空 `resourceList` / `p0ResourceSet` / `loadingList`）|
| `bool IsHighResourceLoaded()` | 高优先级资源是否加载完成 |
| `bool IsAllResourceLoaded()` | 全部资源是否加载完成 |
| `void LoadBundleAssetsAsync(string bundleName, string assetName, Action<UnityEngine.Object> onLoaded, Action onFinish, bool unload = true)` | 异步加载 Bundle 内资源（assetName 为 null 时加载 Bundle 全部资源）；Simulation 分支走 AssetDatabase 路径 |
| `void LoadFloatAssetAsync(string name, Action<UnityEngine.Object> callback, bool unLoad = true)` | 加载单个独立 Bundle 的 float 资源（bundle 名 = asset 名，内部 `name.ToLower()`）；若为 GameObject 类型则自动 `Instantiate` 并返回实例 |
| `void LoadFloatAssetAsync(string bundle, string asset, Action<UnityEngine.Object> callback, bool unLoad = true)` | 指定 bundle 与 asset 的 float 资源加载（bundle 名转小写）|
| `UnityEngine.Object Load(string stage, Tag tag, string name, string path)` | 同步加载框架基础资源（基于 `Resources.Load(path)`），失败抛异常并写入缓存 |
| `void GenerateAssetBundleMap()` | 重建 `assetBundleMap`（Prefab 路径 → Bundle 名），仅运行时路径生效 |
| `void ReorderDownloadListByAssetName(string assetName)` | 按资源名（Prefab）找到 Bundle 并重排下载顺序 |
| `void ReorderDownloadListByBundleName(string bundleName)` | 按 Bundle 名把对应未加载项提到加载队列队首 |
| `string GetKeyByGroupTag(string group, Tag tag)` | 生成缓存桶键 `"{group}-{tag}"` |
| `Dictionary<string, ResourceObject> GetResourceTable(string group, Tag tag)` | 获取（不存在则创建）某组某 Tag 的资源表 |
| `void SetObject(string group, Tag tag, ResourceObject resourceObject)` | 写入缓存并触发 `OnObjectLoaded` |
| `ResourceObject GetObject(Tag tag, string name)` | 从当前 `Group` 下该 Tag 表中取缓存对象；未命中返回 null |
| `ResourceObject[] GetAssets<T>(Tag tag, string bundleName)` | 获取某 Bundle 下类型匹配 `T` 的资源数组 |
| `void ListenObject(string name, Action<ResourceObject> callback)` | 监听对象就绪回调（已缓存立即回调，否则登记待 `OnObjectLoaded` 触发）|
| `void OnObjectLoaded(string name, ResourceObject obj)` | 触发该对象名对应的监听回调 |
| `void DestroyObject(Tag tag, string name)` | 销毁缓存对象实例并从表移除 |
| `void ClearObjects(string group)` | 清理某组资源（当前为占位实现）|
| `void ClearResources()` | 清理当前 stage 资源（当前为占位实现）|
| `void FixShaders(IProgressResult<float, IBundle> br)` | 编辑器（Win/OSX Editor）下刷新材质 shader，防止编辑器 shader 异常 |

### 11.5 数据模型

**ResourceItem**（加载任务描述）

```csharp
public class ResourceItem {
    public string name;
    public ResourceSystem.Tag tag;
    public string path;      // 通常为 Bundle 名 / 资源路径
    public int priority;     // 0: 初始资源；-1: 不加载；>0: 顺序
    public UnityEngine.Object prefab = null;  // 加载完成后的对象
    public bool loading = false;
    public Action onLoaded;
    public ResourceItem(string name, ResourceSystem.Tag tag, string path, int priority);
}
```

**ResourceObject**（结果缓存）

```csharp
public class ResourceObject {
    public string bundleName;
    public string assetName;
    public Object assetObject;
    public ResourceObject(string bundleName, string assetName, Object assetObject);
}
```

### 11.6 辅助类

| 类 | 父类 / 接口 | 说明 |
|----|-------------|------|
| `Singleton<T>` | `MonoBehaviour` | 泛型单例基类；惰性创建 + `DontDestroyOnLoad` |
| `GBundleBuilder` | `AbstractLoaderBuilder` | 自定义加载器工厂；来源链 缓存→Persistent→StreamingAssets→URL；支持 `IStreamDecryptor` 解密 |
| `GMappingPathInfoParser` | `IPathInfoParser, IManifestUpdatable` | 运行时 资源路径→Bundle 名 映射（基于 `BundleManifest`）|
| `GSimulationAutoMappingPathInfoParser` | `IPathInfoParser`（仅编辑器）| 编辑器模拟映射（基于 `AssetDatabase` 的 Bundle 名）|
| `SimulationSetting` | `Loxodon.Framework.Bundles`（仅编辑器）| `IsSimulationMode` 开关（`Loxodon::Framework::Bundle::SimulateInEditor`）|

### 11.7 常用流程梳理

```csharp
// 1. 获取并初始化
var rs = ResourceSystem.Instance;
rs.Init("framework");

// 2. 加入任务（可多次）
rs.AddResourceItem(new ResourceItem("MainPanel", ResourceSystem.Tag.UI,  "uipanels/main", 0));
rs.AddResourceItem(new ResourceItem("Hero",      ResourceSystem.Tag.Model,"hero",          3));

// 3. 开始加载（可传回调）
rs.StartLoading(e => Debug.Log("high done"), e => Debug.Log("all done"));

// 4. 随时取用 / 监听 / 释放
ResourceObject ro = rs.GetObject(ResourceSystem.Tag.UI, "MainPanel");
rs.ListenObject("Hero", r => Debug.Log($"{r.assetName} ready"));
```

---

*本手册依据 `GGResource` 仓库真实源码编写，所有方法名、参数、枚举值与源码一致。*
