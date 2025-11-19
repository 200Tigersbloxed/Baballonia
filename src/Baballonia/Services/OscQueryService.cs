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

/// <summary>
/// Find the first VRChat OSCQuery server on a local networkw
/// </summary>
/// <param name="logger"></param>
/// <param name="oscRecvService"></param>
/// <param name="localSettingsService"></param>
/// <param name="vrChatService"></param>
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
    private string _prevIp = IPAddress.Loopback.ToString();
    private int _prevInPort = 8889;
    private int _prevOutPort = 8888;
    private string _tempOSCQueryIp = IPAddress.Loopback.ToString();
    private int _tempOSCQueryPort = 0;

    private static readonly Regex VrChatClientRegex = new(@"VRChat-Client-[A-Za-z0-9]{6}$", RegexOptions.Compiled);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!localSettingsService.ReadSetting<bool>("VRC_UseVRCFaceTracking")) return Task.CompletedTask;
        _prevIp = localSettingsService.ReadSetting<string>("OSCAddress");
        _prevInPort = localSettingsService.ReadSetting<int>("OSCOutPort");
        _prevInPort = localSettingsService.ReadSetting<int>("OSCInPort");
        localSettingsService.SaveSetting("OSCOutPort", 9000);
        localSettingsService.SaveSetting("OSCInPort", 9001);

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

        oscRecvService.OnMessageReceived += message =>
        {
            if (message.Address == "/avatar/change" && _tempOSCQueryPort > 0)
            {
                vrChatService.PullParametersFromOSCAddress(_tempOSCQueryIp, _tempOSCQueryPort);
            }
        };

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

    /// <summary>
    /// Polls VRChat clients until we have found one
    /// This needs to run for the duration of the app's lifetime
    /// </summary>
    /// <returns></returns>
    private void StartAutoRefreshServices(double interval = 5000)
    {
        logger.LogInformation("OSCQuery start StartAutoRefreshServices");

        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    _serviceWrapper.RefreshServices();
                    ScanForVRChatClients();
                }
                catch (Exception)
                {
                    // Ignore
                }
                await Task.Delay(TimeSpan.FromMilliseconds(interval));
            }
        });
    }

    private bool ScanForVRChatClients()
    {
        if (_profiles.Count == 0) return true;

        try
        {
            // Only set the VRC once
            var vrcProfile = _profiles.First(profile => VrChatClientRegex.IsMatch(profile.name));
            var vrcIp = vrcProfile.address.ToString();
            if (_tempOSCQueryIp == vrcIp) return true;

            _tempOSCQueryIp = vrcProfile.address.ToString();
            _tempOSCQueryPort = vrcProfile.port;
            vrChatService.PullParametersFromOSCAddress(_tempOSCQueryIp, _tempOSCQueryPort);
            int vrcRecvPort = localSettingsService.ReadSetting<int>("OSCInPort"); // 9001~
            oscRecvService.UpdateTarget(new IPEndPoint(IPAddress.Any, vrcRecvPort));
        }
        catch (InvalidOperationException)
        {
            // No matching element, continue
        }
        catch (Exception ex)
        {
            logger.LogError($"Unhandled error in OSCQueryService: {ex}");
        }

        return false;
    }

    public override void Dispose()
    {
        localSettingsService.SaveSetting("OSCAddress", _prevIp);
        localSettingsService.SaveSetting("OSCInPort", _prevInPort);
        localSettingsService.SaveSetting("OSCOutPort", _prevOutPort);

        if (_serviceWrapper == null) return;

        _serviceWrapper.OnOscQueryServiceAdded -= AddProfileToList;
        _serviceWrapper.Dispose();
    }
}
