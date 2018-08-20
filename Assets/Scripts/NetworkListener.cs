using Nethereum.JsonRpc.UnityClient;
using System.Collections;
using System.Numerics;
using UnityEngine;
using UnityEngine.Events;

public class NetworkListener : MonoBehaviour {
    public readonly string NODE_ADDRESS = "https://mainnet.infura.io";
    
    public class NewTxEvent : UnityEvent<string,string> { };
    public NewTxEvent OnNewTx { get; private set; }

    private BigInteger currentHeight = 0;

    private IEnumerator listeningCoroutine = null;
    private bool listening;

	// Use this for initialization
	void Start () {
        OnNewTx = new NewTxEvent();
        StartCoroutine(Init());
	}

    private void OnDestroy()
    {
        listening = false;
        if(listeningCoroutine != null) StopCoroutine(listeningCoroutine);
    }

    IEnumerator Init()
    {
        var blockNbRequest = new EthBlockNumberUnityRequest(NODE_ADDRESS);
        yield return blockNbRequest.SendRequest();
        currentHeight = blockNbRequest.Result.Value - 200;

        listening = true;
        listeningCoroutine = ListenForNewBlock();

        StartCoroutine(listeningCoroutine);
    }

    IEnumerator ListenForNewBlock()
    {
        while (listening)
        {
            var blockNbRequest = new EthBlockNumberUnityRequest(NODE_ADDRESS);
            yield return blockNbRequest.SendRequest();

            while(blockNbRequest.Result.Value > currentHeight)
            {
                yield return StartCoroutine(ProcessNewBlock(currentHeight));
            }

            yield return new WaitForSeconds(20);
        }
    }

    IEnumerator ProcessNewBlock(BigInteger height)
    {
        var getBlockRequest = new EthGetBlockWithTransactionsByNumberUnityRequest(NODE_ADDRESS);
        yield return getBlockRequest.SendRequest(new Nethereum.Hex.HexTypes.HexBigInteger(height));

        if (getBlockRequest.Result == null || getBlockRequest.Exception != null) yield break;

        foreach( var tx in getBlockRequest.Result.Transactions)
        {
            if (tx.To == null || tx.From == null) continue;

            OnNewTx.Invoke(tx.From, tx.To);

            yield return null;
        }

        currentHeight++;
    }
}
