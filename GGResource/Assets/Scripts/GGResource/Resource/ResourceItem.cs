using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ResourceItem
{
    public string name;
    public ResourceSystem.Tag tag;
    public string path;
    public int priority;        // 0: 初始资源；-1: 不加载；>0: 加载顺序。
    public UnityEngine.Object prefab = null;
    public bool loading = false;
    public ResourceItem(string name, ResourceSystem.Tag tag, string path, int priority)
    {
        this.name = name;
        this.tag = tag;
        this.path = path;
        this.priority = priority;
        this.prefab = null;
    }
    public Action onLoaded;
};
