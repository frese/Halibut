using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Halibut.Transport;
using Halibut.Transport.Protocol;

namespace Halibut
{
    public class HalibutRuntime : IHalibutRuntime
    {
        public static readonly string DefaultFriendlyHtmlPageContent = "<html><body><p>Hello!</p></body></html>";
        readonly ConcurrentDictionary<Uri, PendingRequestQueue> queues = new ConcurrentDictionary<Uri, PendingRequestQueue>();
        readonly X509Certificate2 serverCertificate;
        readonly List<IDisposable> listeners = new List<IDisposable>();
        readonly HashSet<string> trustedThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<Uri, ServiceEndPoint> routeTable = new ConcurrentDictionary<Uri, ServiceEndPoint>();
        readonly ServiceInvoker invoker;
        readonly LogFactory logs = new LogFactory();
        readonly ConnectionPool<ServiceEndPoint, IConnection> pool = new ConnectionPool<ServiceEndPoint, IConnection>();
        readonly PollingClientCollection pollingClients = new PollingClientCollection();
        string friendlyHtmlPageContent = DefaultFriendlyHtmlPageContent;
        Dictionary<string, string> friendlyHtmlPageHeaders = new Dictionary<string, string>();

        public HalibutRuntime(X509Certificate2 serverCertificate) : this(new NullServiceFactory(), serverCertificate)
        {
        }

        public HalibutRuntime(IServiceFactory serviceFactory, X509Certificate2 serverCertificate)
        {
            this.serverCertificate = serverCertificate;
            invoker = new ServiceInvoker(serviceFactory);
        }

        public LogFactory Logs
        {
            get { return logs; }
        }

        PendingRequestQueue GetQueue(Uri target)
        {
            return queues.GetOrAdd(target, u => new PendingRequestQueue(logs.ForEndpoint(target)));
        }

        public int Listen()
        {
            return Listen(0);
        }

        public int Listen(int port)
        {
            return Listen(new IPEndPoint(IPAddress.IPv6Any, port));
        }

        public int Listen(IPEndPoint endpoint)
        {
            var listener = new SecureListener(endpoint, serverCertificate, ListenerHandler, IsTrusted, logs, () => friendlyHtmlPageContent, () => friendlyHtmlPageHeaders);
            listeners.Add(listener);
            return listener.Start();
        }
#if HAS_WEB_SOCKET_LISTENER
        public void ListenWebSocket(string endpoint)
        {
            var listener = new SecureWebSocketListener(endpoint, serverCertificate, ListenerHandler, IsTrusted, logs, () => friendlyHtmlPageContent, () => friendlyHtmlPageHeaders);
            listeners.Add(listener);
            listener.Start();
        }
#endif

        Task ListenerHandler(MessageExchangeProtocol obj)
        {
            return obj.ExchangeAsServerAsync(
                HandleIncomingRequest,
                id => GetQueue(id.SubscriptionId));
        }

        public void Poll(Uri subscription, ServiceEndPoint endPoint)
        {
            ISecureClient client;
            var log = logs.ForEndpoint(endPoint.BaseUri);
            if (endPoint.IsWebSocketEndpoint)
            {
#if HAS_SERVICE_POINT_MANAGER
                client = new SecureWebSocketClient(endPoint, serverCertificate, log, pool);
#else
                throw new NotImplementedException("Web Sockets are not available on this platform");
#endif
            }
            else
            {
                client = new SecureClient(endPoint, serverCertificate, log, pool);
            }
            pollingClients.Add(new PollingClient(subscription, client, HandleIncomingRequest, log));
        }

        public ServiceEndPoint Discover(Uri uri)
        {
            return Discover(new ServiceEndPoint(uri, null));
        }

        public ServiceEndPoint Discover(ServiceEndPoint endpoint)
        {
            var client = new DiscoveryClient();
            return client.Discover(endpoint);
        }

        public TService CreateClient<TService>(string endpointBaseUri, string publicThumbprint)
        {
            return CreateClient<TService>(new ServiceEndPoint(endpointBaseUri, publicThumbprint));
        }

        public TService CreateClient<TService>(ServiceEndPoint endpoint)
        {
#if HAS_REAL_PROXY
            return (TService)new HalibutProxy(SendOutgoingRequest, typeof(TService), endpoint).GetTransparentProxy();
#else
            var proxy = DispatchProxy.Create<TService, HalibutProxy>();
            (proxy as HalibutProxy).Configure(SendOutgoingRequest, typeof(TService), endpoint);
            return proxy;
#endif
        }

        ResponseMessage SendOutgoingRequest(RequestMessage request)
        {
            var endPoint = request.Destination;

            switch (endPoint.BaseUri.Scheme.ToLowerInvariant())
            {
                case "https":
                    return SendOutgoingHttpsRequest(request);
                case "poll":
                    return SendOutgoingPollingRequest(request);
                default: throw new ArgumentException("Unknown endpoint type: " + endPoint.BaseUri.Scheme);
            }
        }

        ResponseMessage SendOutgoingHttpsRequest(RequestMessage request)
        {
            var client = new SecureClient(request.Destination, serverCertificate, logs.ForEndpoint(request.Destination.BaseUri), pool);

            ResponseMessage response = null;
            client.ExecuteTransaction(protocol =>
            {
                response = protocol.ExchangeAsClient(request);
            });
            return response;
        }

        ResponseMessage SendOutgoingPollingRequest(RequestMessage request)
        {
            var queue = GetQueue(request.Destination.BaseUri);
            return queue.QueueAndWait(request);
        }

        ResponseMessage HandleIncomingRequest(RequestMessage request)
        {
            return invoker.Invoke(request);
        }

        public void Trust(string clientThumbprint)
        {
            lock (trustedThumbprints)
                trustedThumbprints.Add(clientThumbprint);
        }

        public void RemoveTrust(string clientThumbprint)
        {
            lock (trustedThumbprints)
                trustedThumbprints.Remove(clientThumbprint);
        }


        public void TrustOnly(IReadOnlyList<string> thumbprints)
        {
            lock (trustedThumbprints)
            {
                trustedThumbprints.Clear();
                foreach(var thumbprint in thumbprints)
                    trustedThumbprints.Add(thumbprint);
            }
        }

        public bool IsTrusted(string remoteThumbprint)
        {
            lock (trustedThumbprints)
                return trustedThumbprints.Contains(remoteThumbprint);
        }

        public void Route(ServiceEndPoint to, ServiceEndPoint via)
        {
            routeTable.TryAdd(to.BaseUri, via);
        }

        public void SetFriendlyHtmlPageContent(string html)
        {
            friendlyHtmlPageContent = html ?? DefaultFriendlyHtmlPageContent;
        }

        public void SetFriendlyHtmlPageHeaders(IEnumerable<KeyValuePair<string, string>> headers)
        {
            friendlyHtmlPageHeaders = headers?.ToDictionary(x => x.Key, x => x.Value) ?? new Dictionary<string, string>();
        }

        public void Dispose()
        {
            pollingClients.Dispose();
            pool.Dispose();
            foreach (var listener in listeners)
            {
                listener.Dispose();
            }
        }

#if HAS_WEB_SOCKET_LISTENER
        public static bool OSSupportsWebSockets => Environment.OSVersion.Version >= new Version(6, 2);
#else
        public static bool OSSupportsWebSockets => false;
#endif
    }
}