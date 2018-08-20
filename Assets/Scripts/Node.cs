using Nethereum.JsonRpc.UnityClient;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using UnityEngine;
using UnityEngine.UI;

public class Node : MonoBehaviour {
    public string Address { get; private set; }
    public int Id { get; private set; }

    [SerializeField]
    private MeshRenderer meshRenderer;
    [SerializeField]
    private Color startingColor;
    [SerializeField]
    private float startingScale;
    [SerializeField]
    private Color biggestColor;
    [SerializeField]
    private float biggestScale;

    public float Weight { get; private set; }

    public void Init(string address, int id)
    {
        Address = address;
        Id = id;
        Weight = 0;
    }

    public void AddTxWeight(float weight)
    {
        this.Weight += weight;
    }

    public void UpdateLook(float graphWeight)
    {
        float globalWeight = Weight / graphWeight;
        meshRenderer.material.color = Color.Lerp(startingColor, biggestColor, globalWeight);
        float scale = Mathf.Lerp(startingScale, biggestScale, globalWeight);
        transform.localScale = new Vector3(scale, scale, scale);
    }

    private void OnMouseDown()
    {
        Debug.Log(Address);
    }
}
