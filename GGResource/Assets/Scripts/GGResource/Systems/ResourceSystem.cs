using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
//using UnityEngine.Networking;
using CsAsync;
//using AssetBundles;
using SimpleJson;
//using LuaInterface;

using Loxodon.Framework.Bundles;
using Loxodon.Framework.Contexts;
using Loxodon.Framework.Security.Cryptography;
using Loxodon.Framework.Asynchronous;

/**
 * 优先从Persistent加载，如果没有，从Streaming加载。
 * 支持异步加载。
 * 支持资源缓存。
 * 要可以根据清单加载。清单用表格维护，按系统模块分表（如UI、特效。。。）。加字段：分组、版本。
 * 支持分批加载。
 * 资源分组：通用，大厅，房间。可获取组加载状态和进度。
 * 加载异常处理。各个系统保障自己的容错。
 * 各个管理系统要在合适的时机Object实例化，在合适的时机进行Object卸载。重要：有的对象要用于持续创建实体，所以要考虑如何存储和释放。
 * TODO: 要支持多线程。
 */
public class ResourceSystem : Singleton<ResourceSystem>
{
    //public enum Stage
    //{
    //    Framework,
    //    Lobby,
    //    Room,
    //    Float,
    //}

    public enum Tag {
        UI,
        Model,
        VFX,
        Other,
    }

    public enum State {
        Unloaded,
        Loading,
        Loaded,
        Failed,
    }

    private string FrameworkBundleName = "";
    private const string ASSETS = "Assets/";

    public Action<Exception> onHighResourceLoaded = null; //0优先级的资源完成加载。
    public Action<Exception> onAllResourceLoaded = null;
    public Action<ResourceItem> onResourceLoaded = null;
    //public LuaFunction onResourceLoadedX = null;

    private IResources resources;

    private bool isHighResourceLoaded = false;
    private bool isAllResourceLoaded = false;

    private List<ResourceItem> resourceList = new List<ResourceItem>(100);
    private HashSet<ResourceItem> p0ResourceSet = new HashSet<ResourceItem>();

    //private ConcurrentQueue<ResourceItem> loadingQueue = null;// new ConcurrentQueue<ResourceItem>();       // 资源加载队列。
    private List<ResourceItem> loadingList = new List<ResourceItem>();

    private Dictionary<string, Dictionary<string, ResourceObject>> resourceBuffer = new Dictionary<string, Dictionary<string, ResourceObject>>();
    private Dictionary<string, List<string>> assetsInBundle = new Dictionary<string, List<string>>();
    private Dictionary<string, Action<ResourceObject>> loadListeners = new Dictionary<string, Action<ResourceObject>>();
    private Dictionary<string, string> assetBundleMap = new Dictionary<string, string>(1000);

    private int loadNumber = 0;
    private int loadTotal = 0;

    private string group = "";

    public string Group {
        get {
            return group;
        }
        set {
            //group = value;
            group = "all";  //TODO: GL-暂时设定为一个组，让所有资源统一管理。将来有了资源释放功能后再改为分组管理。
        }
    }

    public int LoadNumber {
        get {
            return loadNumber;
        }
    }

    public int LoadTotal
    {
        get
        {
            return loadTotal;
        }
    }

    private void Awake()
    {

        //foreach (string stage in Enum.GetValues(typeof(Stage)))
        //{
        //    foreach (Tag tagType in Enum.GetValues(typeof(Tag)))
        //    {
        //        string key = GetKeyByStageTag(stage, tagType);
        //        resourceBuffer[key] = new Dictionary<string, ResourceObject>();
        //    }
        //    loadingState[stage] = State.Unloaded;
        //}
    }

    public void Init(string FrameworkBundleName)
    {
        this.FrameworkBundleName = FrameworkBundleName;
        this.resources = CreateResources();

        ApplicationContext context = Context.GetApplicationContext();
        context.GetContainer().Unregister<IResources>();
        context.GetContainer().Register<IResources>(this.resources);
    }

    private void Update()
    {
    }

    IResources CreateResources()
    {
//        IResources resources = null;
#if UNITY_EDITOR
        if (SimulationSetting.IsSimulationMode)
        {
            Debug.Log("Use SimulationResources. Run In Editor");

            /* Create a PathInfoParser. */
            //IPathInfoParser pathInfoParser = new SimplePathInfoParser("@");
            IPathInfoParser pathInfoParser = new GSimulationAutoMappingPathInfoParser();

            /* Create a BundleManager */
            IBundleManager manager = new SimulationBundleManager();

            /* Create a BundleResources */
            resources = new SimulationResources(pathInfoParser, manager);
        }
        else
#endif
        {
            /* Create a BundleManifestLoader. */
            IBundleManifestLoader manifestLoader = new BundleManifestLoader();

            /* Loads BundleManifest. */
            string manifestFilePath = BundleUtil.GetStorableDirectory() + BundleSetting.ManifestFilename;
            if (!File.Exists(manifestFilePath)) {
                manifestFilePath = BundleUtil.GetReadOnlyDirectory() + BundleSetting.ManifestFilename;
                //if (!BetterStreamingAssets.FileExists(manifestFilePath)) {
                //    throw new Exception("包里和本地都找不到manifest文件！-" + manifestFilePath);
                //}
            }

            BundleManifest manifest = manifestLoader.Load(manifestFilePath);

            //manifest.ActiveVariants = new string[] { "", "sd" };
            manifest.ActiveVariants = new string[] { "", "hd" };

            /* Create a PathInfoParser. */
            //IPathInfoParser pathInfoParser = new SimplePathInfoParser("@");
            IPathInfoParser pathInfoParser = new GMappingPathInfoParser(manifest);

            /* Create a BundleLoaderBuilder */
            //ILoaderBuilder builder = new WWWBundleLoaderBuilder(new Uri(BundleUtil.GetReadOnlyDirectory()), false);

            /* AES128_CBC_PKCS7 */
            //RijndaelCryptograph rijndaelCryptograph = new RijndaelCryptograph(128, Encoding.ASCII.GetBytes(this.key), Encoding.ASCII.GetBytes(this.iv));
            //IStreamDecryptor decryptor = CryptographUtil.GetDecryptor(Algorithm.AES128_CBC_PKCS7, Encoding.ASCII.GetBytes(this.key), Encoding.ASCII.GetBytes(this.iv));
            // 不加密

            /* Use a custom BundleLoaderBuilder */
            ILoaderBuilder builder = new GBundleBuilder(new System.Uri(BundleUtil.GetReadOnlyDirectory()), false, null);

            /* Create a BundleManager */
            IBundleManager manager = new BundleManager(manifest, builder);

            /* Create a BundleResources */
            resources = new BundleResources(pathInfoParser, manager);
        }
        GenerateAssetBundleMap();
        return resources;
    }

    public bool IsHighResourceLoaded() {
        return isHighResourceLoaded;
    }

    public bool IsAllResourceLoaded() {
        return isAllResourceLoaded;
    }

    public ResourceObject[] GetAssets<T>(Tag tag, string bundleName) {
        if (!assetsInBundle.ContainsKey(bundleName)) {
            return null;
        }

        List<ResourceObject> assetListByType = new List<ResourceObject>();
        List<string> assetList = assetsInBundle[bundleName];
        foreach (string asset in assetList) {
            ResourceObject resourceObject = GetObject(tag, asset);
            if (resourceObject != null && resourceObject.assetObject is T) {
                assetListByType.Add(resourceObject);
            }
        }
        return assetListByType.ToArray();
    }

    //public void DoLoading() {
    //    //TODO: 一次处理的数量要有限制。
    //    int resourceCount = loadingQueue.Count;

    //    while (loadingQueue != null && loadingQueue.Count > 0)
    //    {
    //        ResourceItem resourceItem;
    //        if (loadingQueue.TryDequeue(out resourceItem))
    //        {
    //            Action onLoadFinish = () => {
    //                resourceCount--;
    //                if (this.onResourceLoaded != null)
    //                {
    //                    this.onResourceLoaded(resourceItem);
    //                }
    //                if (p0ResourceSet.Count > 0)
    //                {
    //                    if (p0ResourceSet.Contains(resourceItem))
    //                    {
    //                        p0ResourceSet.Remove(resourceItem);
    //                        if (p0ResourceSet.Count == 0)
    //                        {
    //                            isHighResourceLoaded = true;
    //                            if (this.onHighResourceLoaded != null)
    //                            {
    //                                this.onHighResourceLoaded(null);
    //                            }
    //                        }
    //                    }
    //                }
    //                if (resourceCount <= 0)
    //                {
    //                    isAllResourceLoaded = true;
    //                    if (this.onAllResourceLoaded != null)
    //                    {
    //                        this.onAllResourceLoaded(null);
    //                    }
    //                }
    //            };
    //            LoadFromBundleAsync(resourceItem, () => {
    //                onLoadFinish();
    //            });
    //            //TODO: 检查是否已经加载过资源，如果加载过，跳过。
    //            //if (resourceItem.name != null)
    //            //{
    //            //    LoadFromBundleAsync(resourceItem, (ResourceItem item)=> {
    //            //        onLoadFinish();
    //            //    });
    //            //}
    //            //else {
    //            //    LoadAllFromBundleAsync(resourceItem, null, (bool done)=> {
    //            //        onLoadFinish();
    //            //    });
    //            //}
    //        }
    //    }
    //}

    public void GenerateAssetBundleMap() {
#if UNITY_EDITOR
        if (SimulationSetting.IsSimulationMode)
        {
            //GL - 编辑器版本不需要做这个优化。
        }
        else
#endif
        {
            AbstractResources aResources = this.resources as AbstractResources;
            if (aResources != null)
            {
                GMappingPathInfoParser pathParser = aResources.PathInfoParser as GMappingPathInfoParser;
                if (pathParser != null) {
                    foreach (BundleInfo bundleInfo in pathParser.BundleManifest.GetAll())
                    {
                        foreach (string assetName in bundleInfo.Assets) {
                            const string pattern = @"/Prefab/";     //GL - 只对Prefab目录下的资源做处理！！！
                            Match m = Regex.Match(assetName, pattern, RegexOptions.IgnoreCase);
                            if (m.Success) {
                                string finalAssetName = Loxodon.Framework.Bundles.Path.GetFileNameWithoutExtension(assetName);
                                assetBundleMap[finalAssetName] = bundleInfo.Name;
                                //Debug.LogWarning("--->Asset:" + finalAssetName + " | Bundle:" + bundleInfo.Name);
                            }
                        }
                    }
                }
            }
        }
    }

    public void ReorderDownloadListByAssetName(string assetName) {
        Debug.LogWarning("===>针对Asset排序:" + assetName);
        if (assetBundleMap.ContainsKey(assetName)) {
            string bundleName = assetBundleMap[assetName];
            ReorderDownloadListByBundleName(bundleName);
        }
    }

    public void ReorderDownloadListByBundleName(string bundleName) {
        Debug.LogWarning("===>针对Bundle排序:" + bundleName);
        if (loadingList.Count > 0)
        {
            Debug.LogWarning("===>排序前:" + loadingList[0].path);
        }
        foreach (ResourceItem resourceItem in loadingList) {
            if (bundleName == resourceItem.path && !resourceItem.loading)
            {
                loadingList.Remove(resourceItem);
                loadingList.Insert(0, resourceItem);
                break;
            }
            else {
                //Debug.LogWarning("===>此资源没有在加载列表中:" + resourceItem.path + "|" + resourceItem.loading.ToString());
            }
        }
        if (loadingList.Count > 0)
        {
            Debug.LogWarning("===>排序后:" + loadingList[0].path);
        }
    }

    public void DoLoading()
    {
        this.loadNumber = 0;
        this.loadTotal = p0ResourceSet.Count;
        //TODO: 一次处理的数量要有限制。
        //int resourceCount = loadingQueue.Count;
        int resourceCount = loadingList.Count;
        Whilst whilst = new Whilst(
            // 测试
            () => {
                //return loadingQueue != null && loadingQueue.Count > 0;
                return loadingList != null && loadingList.Count > 0;
            },
            // 执行
            (Action<Exception> callback) => {
                ResourceItem resourceItem = loadingList[0];
                loadingList.RemoveAt(0);
                if (resourceItem != null)
                //ResourceItem resourceItem;
                //if (loadingQueue.TryDequeue(out resourceItem))
                {
                    Action onLoadFinish = () =>
                    {
                        this.loadNumber++;
                        resourceCount--;
                        resourceItem.loading = false;
                        if (p0ResourceSet.Count > 0)
                        {
                            if (p0ResourceSet.Contains(resourceItem))
                            {
                                p0ResourceSet.Remove(resourceItem);
                                if (p0ResourceSet.Count == 0)
                                {
                                    isHighResourceLoaded = true;
                                    if (this.onHighResourceLoaded != null)
                                    {
                                        this.onHighResourceLoaded(null);
                                    }
                                }
                            }
                        }
                        if (resourceCount <= 0)
                        {
                            isAllResourceLoaded = true;
                            if (this.onAllResourceLoaded != null)
                            {
                                this.onAllResourceLoaded(null);
                            }
                        }
                        callback(null);
                    };
                    Action<ResourceItem> onAssetLoaded = (ResourceItem loadedResourceItem) => {
                        //if (this.onResourceLoadedX != null)
                        //{
                        //    this.onResourceLoadedX.Call(loadedResourceItem);
                        //}
                        if (this.onResourceLoaded != null)
                        {
                            this.onResourceLoaded(loadedResourceItem);
                        }
                    };
                    LoadFromBundleAsync(resourceItem, onAssetLoaded, onLoadFinish);
                    //TODO: 检查是否已经加载过资源，如果加载过，跳过。
                    //if (resourceItem.name != null)
                    //{
                    //    LoadFromBundleAsync(resourceItem, (ResourceItem item)=> {
                    //        onLoadFinish();
                    //    });
                    //}
                    //else {
                    //    LoadAllFromBundleAsync(resourceItem, null, (bool done)=> {
                    //        onLoadFinish();
                    //    });
                    //}
                }
                else {
                    callback(new Exception("Get ResourceItem error!"));
                }
            },
            // 结果
            (Exception e) => {
                //if (e != null)
                //{
                //    GameDebugger.sPushLog(e.ToString());
                //}
                //else
                //{
                //    GameDebugger.sPushLog("Finish loading tables!");
                //}
            });
    }

    /**
     * 用于分组加载的封装。
     */
    private void LoadFromBundleAsync(ResourceItem resourceItem, Action<ResourceItem> onLoaded, Action callback)
    {
        Debug.LogFormat("准备加载bundle: ({0}-{1})", resourceItem.name, resourceItem.path);
        if (assetsInBundle.ContainsKey(resourceItem.path))
        {
            Debug.LogWarningFormat("此bundle已经加载过，不需要重复加载了");
            if (callback != null)
            {
                callback();
            }
            return;
        }
        resourceItem.loading = true;
        LoadBundleAssetsAsync(resourceItem.path, resourceItem.name, (UnityEngine.Object prefab) => {
            resourceItem.name = Loxodon.Framework.Bundles.Path.GetFileNameWithoutExtension(prefab.name);
            resourceItem.prefab = prefab;
            if (!assetsInBundle.ContainsKey(resourceItem.path)) {
                assetsInBundle[resourceItem.path] = new List<string>();
            }
            assetsInBundle[resourceItem.path].Add(resourceItem.name);
            SetObject(this.Group, resourceItem.tag, new ResourceObject(resourceItem.path, resourceItem.name, resourceItem.prefab));
            if (onLoaded != null)
            {
                onLoaded(resourceItem);
            }
        }, callback, false);
    }


    public string GetKeyByGroupTag(string group, Tag tag) {
        string key = group + "-" + tag.ToString();
        return key;
    }

    public bool Initialized() {
        return true;
    }

    public Dictionary<string, ResourceObject> GetResourceTable(string group, Tag tag) {
        Dictionary<string, ResourceObject> resourceTable = null;
        string key = GetKeyByGroupTag(group, tag);
        if (!resourceBuffer.ContainsKey(key))
        {
            resourceTable = new Dictionary<string, ResourceObject>();
            resourceBuffer[key] = resourceTable;
        }
        else {
            resourceTable = resourceBuffer[key];
        }
        return resourceTable;
    }

    public void SetObject(string group, Tag tag, ResourceObject resourceObject) {
        if (resourceObject == null) {
            return;
        }
        Dictionary<string, ResourceObject> resourceTable = GetResourceTable(group, tag);
        if (resourceTable.ContainsKey(resourceObject.assetName)) {
            ResourceObject oldObj = resourceTable[resourceObject.assetName];
            if (oldObj != null) {
                //TODO: GL - 如何释放资源？
                //Resources.UnloadAsset(oldObj);
                //Destroy(oldObj);
            }
        }
        resourceTable[resourceObject.assetName] = resourceObject;
        OnObjectLoaded(resourceObject.assetName, resourceObject);
    }

    public ResourceObject GetObject(Tag tag, string name) {
        Dictionary<string, ResourceObject> resourceTable = GetResourceTable(this.Group, tag);
        if (resourceTable.ContainsKey(name))
        {
            return resourceTable[name];
        }
        return null;
    }

    public void ListenObject(string name, Action<ResourceObject> callback)
    {
        ResourceObject resObject = GetObject(Tag.UI, name);
        if (resObject != null)
        {
            if (callback != null) {
                callback(resObject);
            }
        }
        else {
            loadListeners[name] = callback;
        }
    }

    public void OnObjectLoaded(string name, ResourceObject obj) {
        if (loadListeners.ContainsKey(name) && loadListeners[name] != null) {
            loadListeners[name](obj);
        }
    }

    public void DestroyObject(Tag tag, string name) {
        string key = GetKeyByGroupTag(this.Group, tag);
        if (!resourceBuffer.ContainsKey(key)) {
            return;
        }
        Dictionary<string, ResourceObject> resourceTable = resourceBuffer[key];
        if (resourceTable.ContainsKey(name))
        {
            ResourceObject obj = resourceTable[name];
            if (obj != null)
            {
                Destroy(obj.assetObject);
                resourceTable.Remove(name);
            }
        }
    }

    public void ClearObjects(string group) {
        foreach (string key in resourceBuffer.Keys) {
            string[] keyItems = key.Split('-');
            if (keyItems.Length > 0) {
                if (keyItems[0] == group) {
                    //TODO: 清理
                }
            }
        }
    }

    //public State GetLoadingState(string stage) {
    //    State state = State.Unloaded;
    //    try
    //    {
    //        state = loadingState[stage];
    //    }
    //    catch (Exception e) {
    //        GameDebugger.sPushLog(e.ToString());
    //    }
    //    return state;
    //}

    //public void SetLoadingState(string stage, State state)
    //{
    //    try
    //    {
    //        State lastState = loadingState[stage];
    //        loadingState[stage] = state;
    //        if (lastState != state)
    //        {
    //            if (OnGroupStateChanged.ContainsKey(stage))
    //            {
    //                if (OnGroupStateChanged[stage] != null)
    //                {
    //                    OnGroupStateChanged[stage](state);
    //                }
    //            }
    //        }
    //    }
    //    catch (Exception e)
    //    {
    //        GameDebugger.sPushLog(e.ToString());
    //    }
    //}

    public void Reset() {
        //loadingCount = 0;
        isHighResourceLoaded = false;
        isAllResourceLoaded = false;
        resourceList.Clear();
        p0ResourceSet.Clear();
        //loadingQueue = new ConcurrentQueue<ResourceItem>();
        loadingList = new List<ResourceItem>();
    }

    public void AddResourceItem(ResourceItem resourceItem) {
        resourceList.Add(resourceItem);
        if (resourceItem.priority == 0) {
            p0ResourceSet.Add(resourceItem);
        }
    }

    /// <summary>
    /// 清理当前stage的资源。
    /// </summary>
    public void ClearResources() {
        //Debug.Assert(false, "To implementation!!!");
    }

    public void StartLoading(Action<Exception> onHighResourceLoaded, Action<Exception> onAllResourceLoaded) {
        this.onHighResourceLoaded = onHighResourceLoaded;
        this.onAllResourceLoaded = onAllResourceLoaded;
        // 找出优先级大于等于0的资源。
        List<ResourceItem> sortResourceList = resourceList.FindAll(FindLoadingResource);
        // 根据优先级排序。
        sortResourceList.Sort(CompareResourcePriority);
        // 将资源加入加载队列。
        foreach(ResourceItem resourceItem in sortResourceList) {
            //loadingQueue.Enqueue(resourceItem);
            loadingList.Add(resourceItem);
        }
        // 开始加载
        DoLoading();
    }

    //------------------------------------------------------ private ----------------------------------------------
    private static bool FindLoadingResource(ResourceItem resourceItem)
    {

        if (resourceItem.priority >= 0)
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    private static int CompareResourcePriority(ResourceItem x, ResourceItem y) {
        if (x == null)
        {
            if (y == null)
            {
                // If x is null and y is null, they're
                // equal. 
                return 0;
            }
            else
            {
                // If x is null and y is not null, y
                // is greater. 
                return -1;
            }
        }
        else
        {
            // If x is not null...
            //
            if (y == null)
            // ...and y is null, x is greater.
            {
                return 1;
            }
            else
            {
                return (x.priority - y.priority);
            }
        }
    }

    public void LoadBundleAssetsAsync(string bundleName, string assetName, System.Action<UnityEngine.Object> onLoaded, System.Action onFinish, bool unload = true)
    {
        Debug.Log("LoadBundleAssetsAsync bundleName: " + bundleName + ", asset:" + assetName);
        string[] assetList = null;
        IProgressResult<float, UnityEngine.Object[]> result = null;
#if UNITY_EDITOR
        if (SimulationSetting.IsSimulationMode)
        {
            if (assetName != null)
            {
                assetList = UnityEditor.AssetDatabase.GetAssetPathsFromAssetBundleAndAssetName(bundleName, assetName);
            }
            else {
                assetList = UnityEditor.AssetDatabase.GetAssetPathsFromAssetBundle(bundleName);
            }
            result = resources.LoadAssetsAsync<UnityEngine.Object>(assetList);
            result.Callbackable().OnProgressCallback(p =>
            {
                //Debug.LogFormat("Progress:{0}%", p * 100);
            });
            result.Callbackable().OnCallback((r) =>
            {
                try
                {
                    if (r.Exception != null) {
                        throw r.Exception;
                    }

                    //Debug.LogWarningFormat("--->b-{0};a-{1}", bundleName, assetName);
                    foreach (UnityEngine.Object template in r.Result)
                    {
                        onLoaded(template);
                    }
                    if (onFinish != null)
                    {
                        onFinish();
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogErrorFormat("LoadBundleAssetsAsync bundleName: " + bundleName + ", asset:" + assetName
                        + ", Load failure.Error:{0}", e);
                }
            });
        }
        else
#endif
        {
            IProgressResult<float, IBundle> bundleResult = resources.LoadBundle(bundleName);

            bundleResult.Callbackable().OnCallback((br) => {
                FixShaders(br);
                if (assetName != null)
                {
                    IProgressResult<float, UnityEngine.Object> assetResult = br.Result.LoadAssetAsync<UnityEngine.Object>(assetName);
                    assetResult.Callbackable().OnCallback((ar) => {
                        onLoaded(ar.Result);
                        onFinish();
                    });
                }
                else {
                    IProgressResult<float, UnityEngine.Object[]> assetResult = br.Result.LoadAllAssetsAsync<UnityEngine.Object>();
                    assetResult.Callbackable().OnCallback((ar) => {
                        foreach (UnityEngine.Object prefab in ar.Result)
                        {
                            onLoaded(prefab);
                        }
                        onFinish();
                    });
                }
            });
        }
    }

    //------------------------------------------------------ public ----------------------------------------------

    /**
     * 这个是用于加载基础（框架）资源的。
     * TODO: Async loading?
     */
    public UnityEngine.Object Load(string stage, Tag tag, string name, string path) {
        UnityEngine.Object obj = Resources.Load(path);
        if (obj == null) {
            throw new Exception(string.Format("Load Resource failed!-{0}-{1}", name, path));
        }
        SetObject(stage, tag, new ResourceObject(this.FrameworkBundleName, name, obj));
        return obj;
    }

    public void LoadFloatAssetAsync(string bundle, string asset, Action<UnityEngine.Object> callback, bool unLoad = true)
    {
        Debug.Assert(callback != null, "callback will not be null!");
        string bundleName = bundle.ToLower();
        LoadBundleAssetsAsync(bundleName, asset, (UnityEngine.Object prefab) => {
            //Debug.LogWarningFormat("F****>{0}-{1}", bundle, asset);
        //StartCoroutine(LoadObjectAsync(bundleName, asset, (UnityEngine.Object prefab) => {
            if (prefab == null)
            {
                //throw new Exception("Load bundle failed!-" + name);
                callback(null);
                return;
            }
            // 判断prefab是否为GameObject类型。
            if (prefab.GetType() == typeof(GameObject))
            {
                GameObject objectInstance = Instantiate(prefab) as GameObject;
                if (objectInstance == null)
                {
                    throw new Exception("Instantiate bundle failed!-" + name);
                }
                callback(objectInstance);
            }
            else
            {
                callback(prefab);
            }
        //}, unLoad));
        }, null, unLoad);
    }

    /**
    * 加载float(随时加载的)资源。
    * 这种资源每一个都独立打成bundle，所以bundle名和asset名保持相同。
    * 如果是GameObject类型的资源，就实例化，实例化之后就释放内存资源。
    */
    public void LoadFloatAssetAsync(string name, Action<UnityEngine.Object> callback,bool unLoad = true)
    {
        string bundleName = name.ToLower();
        LoadFloatAssetAsync(bundleName, name, callback, unLoad);
    }

    //public void LoadFloatAssetAsyncX(string bundle, string asset, LuaFunction callback)
    //{
    //    LoadFloatAssetAsync(bundle, asset, (UnityEngine.Object assetObject) => {
    //        callback.Call(assetObject);
    //    });
    //}

    /**
     * 参考接口：LoadFloatAssetAsync
     */
    //public void LoadFloatAssetAsyncX(string name, LuaFunction callback)
    //{
    //    string bundleName = name.ToLower();
    //    LoadFloatAssetAsync(bundleName, name, (UnityEngine.Object assetObject) => {
    //        callback.Call(assetObject);
    //    });
    //}

    /**
     * 此方法只是针对编辑器环境做的一种临时修复方案，确保shader不会出问题。
     */
    public void FixShaders(IProgressResult<float, IBundle> br)
    {
        if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.OSXEditor)
        {
            return;
        }
        IProgressResult<float, UnityEngine.Material[]> matResult = br.Result.LoadAllAssetsAsync<UnityEngine.Material>();
        matResult.Callbackable().OnCallback((ar) => {
            foreach (Material material in ar.Result)
            {
                var shaderName = material.shader.name;
                //Debug.LogWarning("-->fix shader:" + shaderName);
                var newShader = Shader.Find(shaderName);
                if (newShader != null)
                {
                    material.shader = newShader;
                }
                else
                {
                    Debug.LogWarning("unable to refresh shader: " + shaderName + " in material " + material.name);
                }
            }
        });
    }
}
