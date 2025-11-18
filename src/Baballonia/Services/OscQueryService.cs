using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Baballonia.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OscCore;
using VRC.OSCQuery;

namespace Baballonia.Services;

public class OscQueryService(
    ILogger<OscQueryService> logger,
    OscRecvService oscRecvService,
    ILocalSettingsService localSettingsService,
    VRCFaceTrackingService vrChatService
    )
    : BackgroundService
{
    private readonly HashSet<OSCQueryServiceProfile> _profiles = [];
    private OSCQueryService _serviceWrapper = null!;
    private string _tempIp = "127.0.0.1";
    private int _tempPort = 8888;

    private static readonly Regex VrChatClientRegex = new(@"VRChat-Client-[A-Za-z0-9]{6}$", RegexOptions.Compiled);
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!localSettingsService.ReadSetting<bool>("VRC_UseVRCFaceTracking")) return Task.CompletedTask;

        var tcpPort = Extensions.GetAvailableTcpPort();
        var udpPort = Extensions.GetAvailableUdpPort();

        _serviceWrapper = new OSCQueryServiceBuilder()
            .WithDiscovery(new MeaModDiscovery())
            .WithHostIP(IPAddress.Loopback)
            .WithTcpPort(tcpPort)
            .WithUdpPort(udpPort)
            .WithServiceName(
                $"VRChat-Client-BabbleApp-{Utils.RandomString()}") // Yes this has to start with "VRChat-Client" https://github.com/benaclejames/VRCFaceTracking/blob/f687b143037f8f1a37a3aabf97baa06309b500a1/VRCFaceTracking.Core/mDNS/MulticastDnsService.cs#L195
            .StartHttpServer()
            .AdvertiseOSCQuery()
            .AdvertiseOSC()
            .Build();

        logger.LogInformation(
            $"Started OSCQueryService {_serviceWrapper.ServerName} at TCP {tcpPort}, UDP {udpPort}, HTTP http://{_serviceWrapper.HostIP}:{tcpPort}");

        _serviceWrapper.AddEndpoint<string>("/avatar/change", Attributes.AccessValues.ReadWrite, ["default"]);
        _serviceWrapper.OnOscQueryServiceAdded += AddProfileToList;

        StartAutoRefreshServices(5000);

        oscRecvService.OnMessageReceived += (OscMessage message) =>
        {
            if (message.Address == "/avatar/change")
            {
                vrChatService.PullParametersFromOSCAddress(_tempIp, _tempPort);
            }
        };

        return Task.CompletedTask;
    }

    private void AddProfileToList(OSCQueryServiceProfile profile)
    {
        if (_profiles.Contains(profile) || profile.port == _serviceWrapper.TcpPort)
        {
            return;
        }
        _profiles.Add(profile);
        logger.LogInformation($"Added {profile.name} to list of OSCQuery profiles, at address http://{profile.address}:{profile.port}");
    }

    private void StartAutoRefreshServices(double interval)
    {
        logger.LogInformation("OSCQuery start StartAutoRefreshServices");

        Task.Run(async () =>
        {
            while (true)
            {
                var useOscQuery = localSettingsService.ReadSetting<bool>("VRC_UseVRCFaceTracking");
                if (useOscQuery)
                {
                    try
                    {
                        _serviceWrapper.RefreshServices();
                        PollVrChatParameters();
                    }
                    catch (Exception)
                    {
                        // Ignore
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(interval));
            }
        });
    }

    private void PollVrChatParameters()
    {
        if (_profiles.Count == 0) return;

        try
        {
            var vrcProfile = _profiles.First(profile => VrChatClientRegex.IsMatch(profile.name));
            var vrcIp = vrcProfile.address.ToString();
            if (_tempIp == vrcIp) return;

            _tempIp = vrcIp;
            _tempPort = vrcProfile.port;
            vrChatService.PullParametersFromOSCAddress(_tempIp, _tempPort);

        }
        catch (InvalidOperationException)
        {
            // No matching element, continue
        }
        catch (Exception ex)
        {
            logger.LogError($"Unhandled error in OSCQueryService: {ex}");
        }
    }

    public override void Dispose()
    {
        if (_serviceWrapper == null) return;

        _serviceWrapper.OnOscQueryServiceAdded -= AddProfileToList;
        _serviceWrapper.Dispose();
    }
}
