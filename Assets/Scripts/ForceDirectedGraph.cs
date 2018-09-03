using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using UnityEngine;

public class ForceDirectedGraph : MonoBehaviour {
    [Header("Prefabs")]
    [SerializeField]
    private Node nodePrefab;
    [SerializeField]
    private NodeLink linkPrefab;

    [Header("Network")]
    [SerializeField]
    private NetworkListener listener;

    [Header("Physic simulation parameters")]
    [SerializeField]
    private float repulsion = 300;
    [SerializeField]
    private float graphStiffness = 5;
    [SerializeField]
    private float damping = 0.5f;
    [SerializeField]
    private float springRelaxedLength = 20;
    [SerializeField]
    private float springForce = 100;
    [SerializeField]
    private float maxVelocity = 50f;
    [SerializeField]
    private float tolerance = 1f;

    [Header("Shader")]
    [SerializeField]
    private ComputeShader graphPhysicsShader;

    private int updateCenterOfMassKernel;
    private int computeSpringForcesKernel;
    private int computeRepulsionForcesKernel;
    private int updatePositionsKernel;

    private uint computeSpringForcesNbThreads;
    private uint computeRepulsionForcesNbThreads;
    private uint updatePositionsNbThreads;

    // GPU structs
    private struct ShaderNode
    {
        public Vector3 transform;
        public Vector3 acceleration;
        public Vector3 velocity;
    }
    private struct ShaderLink
    {
        public int fromIndex;
        public int toIndex;
    }

    private ShaderNode[] shaderNodes = null;
    private ComputeBuffer shaderNodesBuffer;
    private int shaderNodesCount = 0;
    private ShaderLink[] shaderLinks = null;
    private ComputeBuffer shaderLinksBuffer;
    private int shaderLinksCount = 0;


    private OrderedDictionary nodes = new OrderedDictionary();
    private List<NodeLink> links = new List<NodeLink>();
    private string targetAddress;

    // Physics (energy reduction)
    private bool converged = false;
    private float step = 1 / 30f;
    private float energy = float.PositiveInfinity;
    private int progress = 0;

    // Use this for initialization
    void Start () {
        // Get the kernel handles
        updateCenterOfMassKernel = graphPhysicsShader.FindKernel("UpdateCenterOfMass");
        computeSpringForcesKernel = graphPhysicsShader.FindKernel("ComputeSpringForces");
        computeRepulsionForcesKernel = graphPhysicsShader.FindKernel("ComputeRepulsionForces");
        updatePositionsKernel = graphPhysicsShader.FindKernel("UpdatePositions");

        uint y, z;
        graphPhysicsShader.GetKernelThreadGroupSizes(computeSpringForcesKernel, out computeSpringForcesNbThreads, out y, out z);
        if (y != 1 || z != 1) throw new UnityException("shader kernels' logic should work on the x dimension only");
        graphPhysicsShader.GetKernelThreadGroupSizes(computeRepulsionForcesKernel, out computeRepulsionForcesNbThreads, out y, out z);
        if (y != 1 || z != 1) throw new UnityException("shader kernels' logic should work on the x dimension only");
        graphPhysicsShader.GetKernelThreadGroupSizes(updatePositionsKernel, out updatePositionsNbThreads, out y, out z);
        if (y != 1 || z != 1) throw new UnityException("shader kernels' logic should work on the x dimension only");

        listener.OnNewTx.AddListener(NewLink);
    }

    // Update is called once per frame
    void Update () {
        if (nodes.Count < 2) return;
        if (converged) return;

        UpdateBuffers();

        float prevEnergy = energy;
        var prevCoordinates = shaderNodes.Select(n => n.transform).ToList();

        ComputeGraph(step);

        energy = shaderNodes.Sum(n => n.acceleration.sqrMagnitude);
        step = UpdateStep(step, energy, prevEnergy);
        var diff = prevCoordinates.Select((vec, index) => (vec - shaderNodes[index].transform).magnitude).Sum();
        if (diff < springRelaxedLength * tolerance)
        {
            converged = true;
            Debug.Log("converged ! step is " + step);
        }

        EyeCandy();
    }

    private void OnDestroy()
    {
        if(shaderLinksBuffer != null) shaderLinksBuffer.Dispose();
        if(shaderNodesBuffer != null) shaderNodesBuffer.Dispose();
    }

    private void ResetConvergence()
    {
        converged = false;
        step = 1 / 30f;
        progress = 0;
    }

    private void NewLink(string fromAddress, string toAddress)
    {
        Node from = null;
        Node to = null;

        if (nodes.Contains(fromAddress)) from = (Node)nodes[fromAddress];
        if (nodes.Contains(toAddress)) to = (Node)nodes[toAddress];

        if (from == null && to == null)
        {
            if (targetAddress != null) return;

            Vector3 newPosition = nodes.Count == 0 ? Vector3.zero : (Vector3)nodes.Values.Cast<Node>().Last().transform.position;

            from = Instantiate(nodePrefab, newPosition + RandomVector2(), Quaternion.identity);
            from.Init(fromAddress, nodes.Count);
            nodes[fromAddress] = from;

            to = Instantiate(nodePrefab, newPosition + RandomVector2(), Quaternion.identity);
            to.Init(toAddress, nodes.Count);
            nodes[toAddress] = to;

            targetAddress = "test";
            ResetConvergence();
        }
        else if (from == null)
        {
            from = Instantiate(nodePrefab, (Vector3)to.transform.position + RandomVector2() * springRelaxedLength, Quaternion.identity);
            from.Init(fromAddress, nodes.Count);
            nodes[fromAddress] = from;

            ResetConvergence();
        }
        else if (to == null)
        {
            to = Instantiate(nodePrefab, (Vector3)from.transform.position + RandomVector2() * springRelaxedLength, Quaternion.identity);
            to.Init(toAddress, nodes.Count);
            nodes[toAddress] = to;

            ResetConvergence();
        }

        if (!links.Any(l => (object.ReferenceEquals(l.to, to) && object.ReferenceEquals(l.from, from)) || (object.ReferenceEquals(l.to, from) && object.ReferenceEquals(l.from, to))))
        {
            var link = Instantiate(linkPrefab);
            link.Init(from, to);
            links.Add(link);
        }

        from.AddTxWeight(0.5f);
        to.AddTxWeight(0.5f);
    }

    private Vector3 RandomVector2()
    {
        return new Vector3(Random.Range(-1f, 1f), Random.Range(-1f, 1f), Random.Range(-1f, 1f));
    }

    /// <summary>
    /// Update the buffers with the current values
    /// </summary>
    void UpdateBuffers()
    {
        // update compute buffers
        if (shaderNodes == null || nodes.Count > shaderNodesCount)
        {
            if (shaderNodes == null || nodes.Count > shaderNodes.Length)
            {
                if (shaderNodesBuffer != null) shaderNodesBuffer.Dispose();

                int newSize = Mathf.CeilToInt(nodes.Count / (float)computeRepulsionForcesNbThreads) * (int)computeRepulsionForcesNbThreads;
                newSize = newSize == 0 ? (int)computeRepulsionForcesNbThreads : newSize;

                var existingNodes = nodes.Values.Cast<Node>().Select(n => new ShaderNode { transform = n.transform.position, velocity = Vector3.zero });
                var emtyPading = new List<ShaderNode>(newSize - nodes.Count);

                shaderNodes = existingNodes.Concat(emtyPading).ToArray();
                shaderNodesBuffer = new ComputeBuffer(shaderNodes.Length, sizeof(float) * 3 * 3);
                shaderNodesBuffer.SetData(shaderNodes);
            }
            else
            {
                for (int i = shaderNodesCount; i < nodes.Count; i++)
                {
                    Node n = nodes.Values.Cast<Node>().ElementAt(i);
                    shaderNodes[i] = new ShaderNode { transform = n.transform.position, velocity = Vector3.zero };
                }

                shaderNodesBuffer.SetData(shaderNodes, shaderNodesCount, shaderNodesCount, nodes.Count - shaderNodesCount);
            }

            shaderNodesCount = nodes.Count;
            graphPhysicsShader.SetInt("NbNodes", shaderNodesCount);
        }

        if (shaderLinks == null || links.Count > shaderLinksCount)
        {
            if (shaderLinks == null || links.Count > shaderLinks.Length)
            {
                if (shaderLinksBuffer != null) shaderLinksBuffer.Dispose();

                int newSize = Mathf.CeilToInt(links.Count / (float)computeSpringForcesNbThreads) * (int)computeSpringForcesNbThreads;
                newSize = newSize == 0 ? (int)computeSpringForcesNbThreads : newSize;

                var existingLinks = links.Select(l => new ShaderLink { toIndex = l.to.Id, fromIndex = l.from.Id });
                var emtyPading = new List<ShaderLink>(newSize - links.Count);

                shaderLinks = existingLinks.Concat(emtyPading).ToArray();
                shaderLinksBuffer = new ComputeBuffer(shaderLinks.Length, sizeof(int) * 2);
                shaderLinksBuffer.SetData(shaderLinks);
            }
            else
            {
                for (int i = shaderLinksCount; i < links.Count; i++)
                {
                    shaderLinks[i] = new ShaderLink { toIndex = links[i].to.Id, fromIndex = links[i].from.Id };
                }

                shaderLinksBuffer.SetData(shaderLinks, shaderLinksCount, shaderLinksCount, links.Count - shaderLinksCount);
            }

            shaderLinksCount = links.Count;
            graphPhysicsShader.SetInt("NbLinks", shaderLinksCount);
        }
    }

    /// <summary>
    /// On-gpu physics processing
    /// </summary>
    /// <param name="timeStep"></param>
    void ComputeGraph(float timeStep)
    {
        // set parameters
        graphPhysicsShader.SetFloat("Repulsion", repulsion);
        graphPhysicsShader.SetFloat("Stiffness", graphStiffness);
        graphPhysicsShader.SetFloat("Damping", damping);
        graphPhysicsShader.SetFloat("MaxVelocity", maxVelocity);
        graphPhysicsShader.SetFloat("SpringLength", springRelaxedLength);
        graphPhysicsShader.SetFloat("K", springForce);
        graphPhysicsShader.SetFloat("TimeStep", timeStep);

        // update center of mass
        /*var centerOfMassBuffer = new ComputeBuffer(1, sizeof(float) * 3);
        float[] lol = new float[3] { 0, 0, 0 };
        centerOfMassBuffer.SetData(lol);

        graphPhysicsShader.SetBuffer(updateCenterOfMassKernel, "nodes", shaderNodesBuffer);
        graphPhysicsShader.SetBuffer(updateCenterOfMassKernel, "GraphCenterOfMass", centerOfMassBuffer);
        graphPhysicsShader.Dispatch(updateCenterOfMassKernel, Mathf.CeilToInt(shaderNodes.Length / (float)computeRepulsionForcesNbThreads), 1, 1);
        centerOfMassBuffer.GetData(lol);
        centerOfMassBuffer.Dispose();
        Debug.Log(string.Format("center of mass : {0} - {1} - {2}", lol[0], lol[1], lol[2]));*/

        graphPhysicsShader.SetBuffer(updateCenterOfMassKernel, "nodes", shaderNodesBuffer);
        graphPhysicsShader.Dispatch(updateCenterOfMassKernel, Mathf.CeilToInt(shaderNodes.Length / (float)computeRepulsionForcesNbThreads), 1, 1);

        // spring computation
        graphPhysicsShader.SetBuffer(computeSpringForcesKernel, "links", shaderLinksBuffer);
        graphPhysicsShader.SetBuffer(computeSpringForcesKernel, "nodes", shaderNodesBuffer);
        graphPhysicsShader.Dispatch(computeSpringForcesKernel, Mathf.CeilToInt(shaderLinks.Length / (float)computeSpringForcesNbThreads), 1, 1);

        // coulomb computation
        graphPhysicsShader.SetBuffer(computeRepulsionForcesKernel, "nodes", shaderNodesBuffer);
        graphPhysicsShader.Dispatch(computeRepulsionForcesKernel, Mathf.CeilToInt(shaderNodes.Length / (float)computeRepulsionForcesNbThreads), 1, 1);

        // position update
        graphPhysicsShader.SetBuffer(updatePositionsKernel, "nodes", shaderNodesBuffer);
        graphPhysicsShader.Dispatch(updatePositionsKernel, Mathf.CeilToInt(shaderNodes.Length / (float)computeRepulsionForcesNbThreads), 1, 1);

        shaderNodesBuffer.GetData(shaderNodes);

        for (int i = 0; i < nodes.Values.Count; i++) nodes.Values.Cast<Node>().ElementAt(i).transform.position = shaderNodes[i].transform;
    }

    float UpdateStep(float currentStep, float energy, float prevEnergy)
    {
        float res = currentStep;

        if(energy < prevEnergy)
        {
            progress += 1;
            if(progress >= 5)
            {
                progress = 0;
                res /= 0.9f;
            }
        }
        else
        {
            progress = 0;
            res *= 0.9f;
        }

        return res;
    }

    void EyeCandy()
    {
        float maxWeightInGraph = nodes.Values.Cast<Node>().Max(n => n.Weight);
        foreach (Node n in nodes.Values) n.UpdateLook(maxWeightInGraph);
    }
}
