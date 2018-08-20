using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class NodeLink : MonoBehaviour {
    [SerializeField] private LineRenderer link;
    [SerializeField] public Node from;
    [SerializeField] public Node to;

    public void Init(Node from, Node to)
    {
        this.from = from;
        this.to = to;

        link.SetPositions(new Vector3[] { from.transform.position, to.transform.position });
    }

    // Update is called once per frame
    private void Update () {
        link.SetPositions(new Vector3[] { from.transform.position, to.transform.position });
        link.startColor = from.gameObject.GetComponent<MeshRenderer>().material.color;
        link.endColor = to.gameObject.GetComponent<MeshRenderer>().material.color;
    }
}
