using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.AesHelper;
using BtmI2p.JsonRpcHelpers.Client;
using BtmI2p.LightCertificates.Lib;
using BtmI2p.MiscUtils;
using BtmI2p.Newtonsoft.Json.Linq;
using BtmI2p.ObjectStateLib;
using LinFu.DynamicProxy;

namespace BtmI2p.OneSideSignedJsonRpc
{
    public class OneSideSignedRequestPacket
    {
        public byte[] JsonRpcRequest;
        public DateTime SentTime;
        public Guid PacketId = Guid.NewGuid();
    }
    public class ServerOneSideSignedFuncsForClient
    {
        public Func<SignedData<OneSideSignedRequestPacket>, Task<byte[]>> ProcessSignedRequestPacket;
        public Func<Task<List<JsonRpcServerMethodInfo>>> GetMethodInfos;
        public Func<Task<DateTime>> GetNowTimeFunc;
    }

    public class ClientOneSideSignedTransport<T1> : IInvokeWrapper, IMyAsyncDisposable 
        where T1 :class
    {
        private ClientOneSideSignedTransport(
            ServerOneSideSignedFuncsForClient transport,
            LightCertificate clientCert,
            AesProtectedByteArray clientCertPass
            )
        {
            _transport = transport;
            _clientCert = clientCert;
            _clientCertPass = clientCertPass;
        }

        private readonly ServerOneSideSignedFuncsForClient _transport;
        private readonly LightCertificate _clientCert;
        private readonly AesProtectedByteArray _clientCertPass;
        public static async Task<ClientOneSideSignedTransport<T1>> CreateInstance(
            ServerOneSideSignedFuncsForClient transport,
            LightCertificate clientCert,
            AesProtectedByteArray clientCertPass,
            CancellationToken cancellationToken,
            List<JsonRpcServerMethodInfo> methodInfos = null
        )
        {
            var result = new ClientOneSideSignedTransport<T1>(
                transport,
                clientCert,
                clientCertPass
                );
            if (methodInfos == null)
            {
                while (true)
                {
                    try
                    {
                        methodInfos = await transport.GetMethodInfos()
                            .ThrowIfCancelled(cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
            if(
                !JsonRpcClientProcessor.CheckRpcServerMethodInfos(
                    typeof (T1),
                    methodInfos
                )
            )
                throw new Exception(
                    "Rpc server method infos not matches with T1"
                );
            result._stateHelper.SetInitializedState();
            return await Task.FromResult(result).ConfigureAwait(false);
        }


        public void BeforeInvoke(InvocationInfo info)
        {
        }

        private T1 _proxy = null;
        private readonly SemaphoreSlim _proxyLockSem = new SemaphoreSlim(1);
        public async Task<T1> GetClientProxy()
        {
            using (_stateHelper.GetFuncWrapper())
            {
                using (await _proxyLockSem.GetDisposable().ConfigureAwait(false))
                {
                    return _proxy ?? (_proxy = (new ProxyFactory()).CreateProxy<T1>(this));
                }
            }
        }

        private async Task<object> DoInvokeImpl(InvocationInfo info)
        {
            using (_stateHelper.GetFuncWrapper())
            {
                var jsonRequest = JsonRpcClientProcessor.GetJsonRpcRequest(info);
                var requestPacket = new OneSideSignedRequestPacket()
                {
                    JsonRpcRequest = Encoding.UTF8.GetBytes(jsonRequest.ToString()),
                    SentTime = await _transport.GetNowTimeFunc().ConfigureAwait(false)
                };
                SignedData<OneSideSignedRequestPacket> signedRequestPacket;
                using (var tempPass = _clientCertPass.TempData)
                {
                    signedRequestPacket = new SignedData<OneSideSignedRequestPacket>(
                        requestPacket,
                        _clientCert,
                        tempPass.Data
                    );
                }
                var serverAnswerData = await _transport.ProcessSignedRequestPacket(
                    signedRequestPacket
                ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                return await JsonRpcClientProcessor.GetJsonRpcResult(
                    JObject.Parse(
                        Encoding.UTF8.GetString(
                            serverAnswerData
                        )
                    ),
                    info
                ).ConfigureAwait(false);
            }
        }

        public object DoInvoke(InvocationInfo info)
        {
            return JsonRpcClientProcessor.DoInvokeHelper(info, DoInvokeImpl);
        }

        public void AfterInvoke(InvocationInfo info, object returnValue)
        {
        }

        private readonly DisposableObjectStateHelper _stateHelper 
            = new DisposableObjectStateHelper("ClientOneSideSignedTransport");
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        public async Task MyDisposeAsync()
        {
            _cts.Cancel();
            await _stateHelper.MyDisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
