using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ResourceObject
{
    public string bundleName;
    public string assetName;
    public Object assetObject;
    // 引用计数：默认 0 表示已缓存但无人持有。Retain 获取时自增，DestroyObject 释放时自减，归零才真正销毁共享 asset。
    public int refCount = 0;

    public ResourceObject(string bundleName, string assetName, Object assetObject) {
        this.bundleName = bundleName;
        this.assetName = assetName;
        this.assetObject = assetObject;
    }
}
