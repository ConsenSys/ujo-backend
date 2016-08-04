using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using edjCase.JsonRpc.Client;
using edjCase.JsonRpc.Core;
using Nethereum.JsonRpc.Client;
using Newtonsoft.Json;

namespace UjoSpike.ArtistWriter.Console
{
    public class IpcClient : IClient, IDisposable
    {
        public JsonSerializerSettings JsonSerializerSettings { get; set; }

        private string ipcPath;

        private object lockingObject = new object();

        private NamedPipeClientStream pipeClient;
        private NamedPipeClientStream GetPipeClient()
        {
            lock (lockingObject)
            {
                try
                {
                    if (pipeClient == null || !pipeClient.IsConnected)
                    {
                        pipeClient = new NamedPipeClientStream(ipcPath);
                      
                        pipeClient.Connect();
                       
                    }
                }
                catch
                {
                    //Connection error we want to allow to retry.
                    pipeClient = null;
                    throw;
                }
            }

            return pipeClient;
        }


        public IpcClient(string ipcPath, JsonSerializerSettings jsonSerializerSettings = null)
        {
            if (jsonSerializerSettings == null)
            {
                jsonSerializerSettings = DefaultJsonSerializerSettingsFactory.BuildDefaultJsonSerializerSettings();
            }
            this.ipcPath = ipcPath;
            this.JsonSerializerSettings = jsonSerializerSettings;
        }

        public async Task<RpcResponse> SendRequestAsync(RpcRequest request, string route = null)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return await this.SendAsync<RpcRequest, RpcResponse>(request).ConfigureAwait(false);
        }

        public Task<RpcResponse> SendRequestAsync(string method, string route = null, params object[] paramList)
        {
            if (string.IsNullOrWhiteSpace(method))
            {
                throw new ArgumentNullException(nameof(method));
            }
            RpcRequest request = new RpcRequest(Guid.NewGuid().ToString(), method, paramList);
            return this.SendRequestAsync(request);
        }

        private async Task<byte[]> ReadResponseStream(NamedPipeClientStream pipeClientStream)
        {
            var buffer = new byte[512];
            
            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    //if the total number of bytes matches 512 for the last Read, it will wait for the next read forever as we don't have a flag for completeness
                    //a wait is in place for this with a 1 second (maybe too long..)
                    //if timesout (false returned) we have to close the pipestream and return the memory stream
                    var read = 0;
                    if (Task.Run(
                            async () =>
                            {
                                read = await pipeClientStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                                ms.Write(buffer, 0, read);
                            }
                            ).Wait(1000))
                        {
                            if (read < 512)
                            {
                                return ms.ToArray();
                            }
                        }
                        else
                        {
                            pipeClientStream.Close();
                            return ms.ToArray();    
                        }
                    }
            }
        }

        private async Task<TResponse> SendAsync<TRequest, TResponse>(TRequest request)
        {
            try
            {
                var pipeClientStream = GetPipeClient();

                string rpcRequestJson = JsonConvert.SerializeObject(request, this.JsonSerializerSettings);
                byte[] requestBytes = Encoding.UTF8.GetBytes(rpcRequestJson);
                await pipeClientStream.WriteAsync(requestBytes, 0, requestBytes.Length).ConfigureAwait(false);

                var responseBytes = await ReadResponseStream(pipeClientStream).ConfigureAwait(false);
                
                var totalMegs = (responseBytes.Length / 1024f) / 1024f;
                if (totalMegs > 10) throw new RpcClientUnknownException("Response exceeds 10MB it will be impossible to parse it");
                string responseJson = Encoding.UTF8.GetString(responseBytes);

                try
                {
                    return JsonConvert.DeserializeObject<TResponse>(responseJson, this.JsonSerializerSettings);
               }
                catch (JsonSerializationException)
                {
                    RpcResponse rpcResponse = JsonConvert.DeserializeObject<RpcResponse>(responseJson, this.JsonSerializerSettings);
                    if (rpcResponse == null)
                    {
                        throw new RpcClientUnknownException(
                        $"Unable to parse response from the ipc server. Response Json: {responseJson}");
                    }
                    throw rpcResponse.Error.CreateException();
                }
            }
            catch (Exception ex) when (!(ex is RpcClientException) && !(ex is RpcException))
            {
                throw new RpcClientUnknownException("Error occurred when trying to send ipc requests(s)", ex);
            }

        }

        #region IDisposable Support
        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if (pipeClient != null)
                    {
                        pipeClient.Close();
                        pipeClient.Dispose();
                    }
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
        #endregion

    }
}