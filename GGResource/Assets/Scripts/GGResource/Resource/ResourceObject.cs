using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ResourceObject
{
    public string bundleName;
    public string assetName;
    public Object assetObject;

    public ResourceObject(string bundleName, string assetName, Object assetObject) {
        this.bundleName = bundleName;
        this.assetName = assetName;
        this.assetObject = assetObject;
    }
}
