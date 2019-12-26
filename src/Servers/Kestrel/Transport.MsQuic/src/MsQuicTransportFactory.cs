// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.MsQuic.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.MsQuic
{
    public class MsQuicTransportFactory : IMultiplexedConnectionListenerFactory
    {
        private MsQuicTrace _log;
        private IHostApplicationLifetime _applicationLifetime;
        private MsQuicTransportOptions _options;

        public MsQuicTransportFactory(IHostApplicationLifetime applicationLifetime, ILoggerFactory loggerFactory, IOptions<MsQuicTransportOptions> options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            var logger = loggerFactory.CreateLogger("Microsoft.AspNetCore.Server.Kestrel.Transport.MsQuic");
            _log = new MsQuicTrace(logger);
            _applicationLifetime = applicationLifetime;
            _options = options.Value;
        }

        public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            var transport = new MsQuicConnectionListener(_options, _applicationLifetime, _log, endpoint);
            await transport.BindAsync();
            return transport;
        }
    }
}
