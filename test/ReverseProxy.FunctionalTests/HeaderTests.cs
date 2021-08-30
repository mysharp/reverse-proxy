// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Xunit;
using Yarp.ReverseProxy.Common;
using Yarp.ReverseProxy.Forwarder;

namespace Yarp.ReverseProxy
{
    public class HeaderTests
    {
        [Fact]
        public async Task ProxyAsync_EmptyRequestHeader_Proxied()
        {
            var refererReceived = new TaskCompletionSource<StringValues>(TaskCreationOptions.RunContinuationsAsynchronously);
            var customReceived = new TaskCompletionSource<StringValues>(TaskCreationOptions.RunContinuationsAsynchronously);

            IForwarderErrorFeature proxyError = null;
            Exception unhandledError = null;

            var test = new TestEnvironment(
                context =>
                {
                    if (context.Request.Headers.TryGetValue(HeaderNames.Referer, out var header))
                    {
                        refererReceived.SetResult(header);
                    }
                    else
                    {
                        refererReceived.SetException(new Exception($"Missing '{HeaderNames.Referer}' header in request"));
                    }

                    if (context.Request.Headers.TryGetValue("custom", out header))
                    {
                        customReceived.SetResult(header);
                    }
                    else
                    {
                        customReceived.SetException(new Exception($"Missing 'custom' header in request"));
                    }


                    return Task.CompletedTask;
                },
                proxyBuilder => { },
                proxyApp =>
                {
                    proxyApp.Use(async (context, next) =>
                    {
                        try
                        {
                            Assert.True(context.Request.Headers.TryGetValue(HeaderNames.Referer, out var header));
                            var value = Assert.Single(header);
                            Assert.True(StringValues.IsNullOrEmpty(value));

                            Assert.True(context.Request.Headers.TryGetValue("custom", out header));
                            value = Assert.Single(header);
                            Assert.True(StringValues.IsNullOrEmpty(value));

                            await next();
                            proxyError = context.Features.Get<IForwarderErrorFeature>();
                        }
                        catch (Exception ex)
                        {
                            unhandledError = ex;
                            throw;
                        }
                    });
                },
                proxyProtocol: HttpProtocols.Http1);

            await test.Invoke(async proxyUri =>
            {
                var proxyHostUri = new Uri(proxyUri);

                using var tcpClient = new TcpClient(proxyHostUri.Host, proxyHostUri.Port);
                await using var stream = tcpClient.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Host: {proxyHostUri.Host}:{proxyHostUri.Port}\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: 0\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Connection: close\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Referer: \r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"custom: \r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
                var buffer = new byte[4096];
                var responseBuilder = new StringBuilder();
                while (true)
                {
                    var count = await stream.ReadAsync(buffer);
                    if (count == 0)
                    {
                        break;
                    }
                    responseBuilder.Append(Encoding.ASCII.GetString(buffer, 0, count));
                }
                var response = responseBuilder.ToString();

                Assert.Null(proxyError);
                Assert.Null(unhandledError);

                Assert.StartsWith("HTTP/1.1 200 OK", response);

                Assert.True(refererReceived.Task.IsCompleted);
                var refererHeader = await refererReceived.Task;
                var referer = Assert.Single(refererHeader);
                Assert.True(StringValues.IsNullOrEmpty(referer));

                Assert.True(customReceived.Task.IsCompleted);
                var customHeader = await customReceived.Task;
                var custom = Assert.Single(customHeader);
                Assert.True(StringValues.IsNullOrEmpty(custom));
            });
        }

        [Fact]
        public async Task ProxyAsync_EmptyResponseHeader_Proxied()
        {
            IForwarderErrorFeature proxyError = null;
            Exception unhandledError = null;

            var test = new TestEnvironment(
                context =>
                {
                    context.Response.Headers.Add(HeaderNames.Referer, "");
                    context.Response.Headers.Add("custom", "");
                    return Task.CompletedTask;
                },
                proxyBuilder => { },
                proxyApp =>
                {
                    proxyApp.Use(async (context, next) =>
                    {
                        try
                        {
                            await next();

                            Assert.True(context.Response.Headers.TryGetValue(HeaderNames.Referer, out var header));
                            var value = Assert.Single(header);
                            Assert.True(StringValues.IsNullOrEmpty(value));

                            Assert.True(context.Response.Headers.TryGetValue("custom", out header));
                            value = Assert.Single(header);
                            Assert.True(StringValues.IsNullOrEmpty(value));

                            proxyError = context.Features.Get<IForwarderErrorFeature>();
                        }
                        catch (Exception ex)
                        {
                            unhandledError = ex;
                            throw;
                        }
                    });
                },
                proxyProtocol: HttpProtocols.Http1);

            await test.Invoke(async proxyUri =>
            {
                var proxyHostUri = new Uri(proxyUri);

                using var tcpClient = new TcpClient(proxyHostUri.Host, proxyHostUri.Port);
                await using var stream = tcpClient.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Host: {proxyHostUri.Host}:{proxyHostUri.Port}\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: 0\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Connection: close\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
                var buffer = new byte[4096];
                var responseBuilder = new StringBuilder();
                while (true)
                {
                    var count = await stream.ReadAsync(buffer);
                    if (count == 0)
                    {
                        break;
                    }
                    responseBuilder.Append(Encoding.ASCII.GetString(buffer, 0, count));
                }
                var response = responseBuilder.ToString();

                Assert.Null(proxyError);
                Assert.Null(unhandledError);

                var lines = response.Split("\r\n");
                Assert.Equal("HTTP/1.1 200 OK", lines[0]);
                // Order varies across vesions.
                // Assert.Equal("Content-Length: 0", lines[1]);
                // Assert.Equal("Connection: close", lines[2]);
                // Assert.StartsWith("Date: ", lines[3]);
                // Assert.Equal("Server: Kestrel", lines[4]);
                Assert.Equal("Referer: ", lines[5]);
                Assert.Equal("custom: ", lines[6]);
                Assert.Equal("", lines[7]);
            });
        }

#if NET
        [Theory]
        [InlineData("http://www.ěščřžýáíé.com", "utf-8")]
        [InlineData("http://www.çáéôîèñøæ.com", "iso-8859-1")]
        public async Task ProxyAsync_RequestWithEncodedHeaderValue(string headerValue, string encodingName)
        {
            var encoding = Encoding.GetEncoding(encodingName);
            var tcs = new TaskCompletionSource<StringValues>(TaskCreationOptions.RunContinuationsAsynchronously);

            IForwarderErrorFeature proxyError = null;
            Exception unhandledError = null;

            var test = new TestEnvironment(
                context =>
                {
                    if (context.Request.Headers.TryGetValue(HeaderNames.Referer, out var header))
                    {
                        tcs.SetResult(header);
                    }
                    else
                    {
                        tcs.SetException(new Exception($"Missing '{HeaderNames.Referer}' header in request"));
                    }
                    return Task.CompletedTask;
                },
                proxyBuilder => { },
                proxyApp =>
                {
                    proxyApp.Use(async (context, next) =>
                    {
                        try
                        {
                            Assert.True(context.Request.Headers.TryGetValue(HeaderNames.Referer, out var header));
                            var value = Assert.Single(header);
                            Assert.Equal(headerValue, value);

                            await next();
                            proxyError = context.Features.Get<IForwarderErrorFeature>();
                        }
                        catch (Exception ex)
                        {
                            unhandledError = ex;
                            throw;
                        }
                    });
                },
                proxyProtocol: HttpProtocols.Http1, headerEncoding: encoding);

            await test.Invoke(async proxyUri =>
            {
                var proxyHostUri = new Uri(proxyUri);

                using var tcpClient = new TcpClient(proxyHostUri.Host, proxyHostUri.Port);
                await using var stream = tcpClient.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Host: {proxyHostUri.Host}:{proxyHostUri.Port}\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: 0\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Connection: close\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Referer: "));
                await stream.WriteAsync(encoding.GetBytes(headerValue));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
                var buffer = new byte[4096];
                var responseBuilder = new StringBuilder();
                while (true)
                {
                    var count = await stream.ReadAsync(buffer);
                    if (count == 0)
                    {
                        break;
                    }
                    responseBuilder.Append(Encoding.ASCII.GetString(buffer, 0, count));
                }
                var response = responseBuilder.ToString();

                Assert.Null(proxyError);
                Assert.Null(unhandledError);

                Assert.StartsWith("HTTP/1.1 200 OK", response);

                Assert.True(tcs.Task.IsCompleted);
                var refererHeader = await tcs.Task;
                var referer = Assert.Single(refererHeader);
                Assert.Equal(headerValue, referer);
            });
        }

        [Theory]
        [InlineData("http://www.ěščřžýáíé.com", "utf-8")]
        [InlineData("http://www.çáéôîèñøæ.com", "iso-8859-1")]
        public async Task ProxyAsync_ResponseWithEncodedHeaderValue(string headerValue, string encodingName)
        {
            var encoding = Encoding.GetEncoding(encodingName);

            var tcpListener = new TcpListener(IPAddress.Loopback, 0);
            tcpListener.Start();
            var destinationTask = Task.Run(async () =>
            {
                using var tcpClient = await tcpListener.AcceptTcpClientAsync();
                await using var stream = tcpClient.GetStream();
                var buffer = new byte[4096];
                var requestBuilder = new StringBuilder();
                while (true)
                {
                    var count = await stream.ReadAsync(buffer);
                    if (count == 0)
                    {
                        break;
                    }

                    requestBuilder.Append(encoding.GetString(buffer, 0, count));

                    // End of the request
                    if (requestBuilder.Length >= 4 &&
                        requestBuilder[^4] == '\r' && requestBuilder[^3] == '\n' &&
                        requestBuilder[^2] == '\r' && requestBuilder[^1] == '\n')
                    {
                        break;
                    }
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: 0\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Connection: close\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Test-Extra: pingu\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Location: "));
                await stream.WriteAsync(encoding.GetBytes(headerValue));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
            });

            IForwarderErrorFeature proxyError = null;
            Exception unhandledError = null;

            using var proxy = TestEnvironment.CreateProxy(HttpProtocols.Http1, false, false, encoding, "cluster1", $"http://{tcpListener.LocalEndpoint}",
                proxyBuilder => { },
                proxyApp =>
                {
                    proxyApp.Use(async (context, next) =>
                    {
                        try
                        {
                            await next();
                            proxyError = context.Features.Get<IForwarderErrorFeature>();
                        }
                        catch (Exception ex)
                        {
                            unhandledError = ex;
                            throw;
                        }
                    });
                },
                (c, r) => (c, r));

            await proxy.StartAsync();

            try
            {
                using var httpClient = new HttpClient();
                using var response = await httpClient.GetAsync(proxy.GetAddress());

                Assert.NotNull(proxyError);
                Assert.Equal(ForwarderError.ResponseHeaders, proxyError.Error);
                var ioe = Assert.IsType<InvalidOperationException>(proxyError.Exception);
                Assert.StartsWith("Invalid non-ASCII or control character in header: ", ioe.Message);
                Assert.Null(unhandledError);

                Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

                Assert.False(response.Headers.TryGetValues(HeaderNames.Location, out _));
                Assert.False(response.Headers.TryGetValues("Test-Extra", out _));

                Assert.True(destinationTask.IsCompleted);
                await destinationTask;
            }
            finally
            {
                await proxy.StopAsync();
                tcpListener.Stop();
            }
        }
#endif
    }
}
