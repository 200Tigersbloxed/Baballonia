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
using VRC.OSCQuery;

namespace Baballonia.Services;

public class OscQueryService(
    ILogger<OscQueryService> logger,
    ILocalSettingsService localSettingsService,
    VRCFaceTrackingService vrChatService
    )
    : BackgroundService
{
    private readonly HashSet<OSCQueryServiceProfile> _profiles = [];
    private OSCQueryService _serviceWrapper = null!;

    private static readonly Regex VrChatClientRegex = new(@"VRChat-Client-[A-Za-z0-9]{6}$", RegexOptions.Compiled);
    private CancellationTokenSource _cancellationTokenSource;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!localSettingsService.ReadSetting<bool>("VRC_UseVRCFaceTracking")) return Task.CompletedTask;
        var ipString = localSettingsService.ReadSetting<string>("OSCAddress");
        var hostIp = IPAddress.Parse(ipString);

        _cancellationTokenSource = new CancellationTokenSource();
        var tcpPort = Extensions.GetAvailableTcpPort();
        var udpPort = Extensions.GetAvailableUdpPort();

        _serviceWrapper = new OSCQueryServiceBuilder()
            .WithDiscovery(new MeaModDiscovery())
            .WithHostIP(hostIp)
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

            var hostIp = localSettingsService.ReadSetting<string>("OSCAddress");
            var vrcIp = vrcProfile.address.ToString();
            if (hostIp != vrcIp)
            {
                localSettingsService.SaveSetting("OSCAddress", vrcIp);
            }

            var hostPort = localSettingsService.ReadSetting<int>("OSCOutPort");
            var vrcPort = vrcProfile.port;
            if (hostPort != vrcPort)
            {
                localSettingsService.SaveSetting("OSCOutPort", vrcPort);
            }

            if (hostIp != vrcIp || hostPort != vrcPort)
            {
                vrChatService.PullParametersFromOSCAddress(vrcIp, vrcPort);
            }
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
        if (localSettingsService.ReadSetting<bool>("VRC_UseVRCFaceTracking"))
        {
            // If we used VRCFaceTracking's OSCQuery navigation,
            // Make sure to reset the original values
            localSettingsService.SaveSetting("OSCAddress", "127.0.0.1");
            localSettingsService.SaveSetting("OSCOutPort", 8888);
        }

        if (_cancellationTokenSource != null)
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
        }

        if (_serviceWrapper != null)
        {
            _serviceWrapper.OnOscQueryServiceAdded -= AddProfileToList;
            _serviceWrapper.Dispose();
        }
    }
}
