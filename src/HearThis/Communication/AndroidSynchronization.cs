// --------------------------------------------------------------------------------------------
#region // Copyright (c) 2016-2025, SIL Global.
// <copyright from='2016' to='2025' company='SIL Global'>
//		Copyright (c) 2016-2025, SIL Global.
//
//		Distributable under the terms of the MIT License (https://sil.mit-license.org/)
// </copyright>
#endregion
// --------------------------------------------------------------------------------------------
using System;
using HearThis.Script;
using HearThis.UI;
using SIL.IO;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;
using DesktopAnalytics;
using L10NSharp;
using SIL.Reporting;
using static System.String;
using System.Diagnostics;

namespace HearThis.Communication
{
	/// <summary>
	/// This class encapsulates the logic around performing synchronization with Android devices.
	/// </summary>
	public static class AndroidSynchronization
	{
		private static string LocalIp ="";
		private const string kHearThisAndroidProductName = "HearThis Android";

		public static void DoAndroidSync(Project project, Form parent)
		{
			if (!project.IsRealProject)
			{
				MessageBox.Show(parent, Format(
					LocalizationManager.GetString("AndroidSynchronization.DoNotUseSampleProject",
					"Sorry, {0} does not yet work properly with the Sample project. Please try a real one.",
					"Param 0: \"HearThis Android\" (product name)"), kHearThisAndroidProductName),
					Program.kProduct);
				return;
			}
			var dlg = new AndroidSyncDialog();

			// WM, debug only!
			// The local IP determination logic does consider network interfaces but it does not zero in
			// on the correct one, at least for my Windows 10 laptop. So before anything else, run the
			// Bloom debug code that lists all network interfaces along with significant attributes of
			// each, including IP addresses (an interface can have several).
			showNetworkInterfaces();

			// To find out what local IP address we should present to Android devices, learn
			// what IP address *currently* supports internet traffic. Then use that address.
			LocalIp = GetIpAddressOfNetworkIface();
			if (LocalIp.Length == 0)
			{
				Debug.WriteLine("AndroidSync.DoAndroidSync, ERROR: can't get local IP address, exiting");
				return;
			}

			// WM, remove --
			//Debug.WriteLine("WM, AndroidSync.DoAndroidSync, calling GetNetworkAddress(parent)");
			//var address = GetNetworkAddress(parent);
			//Debug.WriteLine("WM, AndroidSync.DoAndroidSync, parent address (IPv4) = " + address.MapToIPv4());

			//if (address == null)
			//	return;

			//dlg.SetOurIpAddress(address.ToString());
			dlg.SetOurIpAddress(LocalIp);

			//Debug.WriteLine("WM, AndroidSync.DoAndroidSync, calling ShowAndroidIpAddress()");
			dlg.ShowAndroidIpAddress(); // AFTER we set our IP address, which may be used to provide a default
			//Debug.WriteLine("WM, AndroidSync.DoAndroidSync, did ShowAndroidIpAddress()");
			dlg.GotSync += (o, args) =>
			{
				try
				{
					bool RetryOnTimeout(WebException ex, string path)
					{
						var response = MessageBox.Show(parent, ex.Message + Environment.NewLine +
							Format(LocalizationManager.GetString(
								"AndroidSynchronization.RetryOnTimeout",
								"Attempting to copy {0}\r\nChoose Abort to stop the sync (but not " +
								"roll back anything already synchronized).\r\nChoose Retry to " +
								"attempt to sync this file again with a longer timeout.\r\n" +
								"Choose Ignore to skip this file and keep the existing one on this " +
								"computer but continue attempting to sync any remaining files. ",
								"Param 0: file path on Android system"),
								path), Program.kProduct, MessageBoxButtons.AbortRetryIgnore);
						switch (response)
						{
							case DialogResult.Abort:
								throw ex;
							case DialogResult.Retry:
								return true;
							case DialogResult.Ignore:
								return false;
							default:
								throw new ArgumentOutOfRangeException();
						}
					}

					Analytics.Track("Sync with Android");
					var theirLink = new AndroidLink(AndroidSyncDialog.AndroidIpAddress,
						RetryOnTimeout);
					var ourLink = new WindowsLink(Program.ApplicationDataBaseFolder);
					var merger = new RepoMerger(project, ourLink, theirLink);
					merger.Merge(project.StylesToSkipByDefault, dlg.ProgressBox);
					//Update info.txt on Android
					var infoFilePath = project.GetProjectRecordingStatusInfoFilePath();
					Debug.WriteLine("WM, AndroidSync.DoAndroidSync, infoFilePath = " + infoFilePath);
					RobustFile.WriteAllText(infoFilePath, project.GetProjectRecordingStatusInfoFileContent());
					var theirInfoTxtPath = project.Name + "/" + Project.InfoTxtFileName;
					theirLink.PutFile(theirInfoTxtPath, File.ReadAllBytes(infoFilePath));
					Debug.WriteLine("WM, AndroidSync.DoAndroidSync, sync complete");
					theirLink.SendNotification("syncCompleted");
					dlg.ProgressBox.WriteMessage("Sync completed successfully");
					//dlg.Close();
				}
				catch (WebException ex)
				{
					string msg;
					Debug.WriteLine("WM, AndroidSync.DoAndroidSync, WebException = " + ex.Status);
					switch (ex.Status)
					{
						case WebExceptionStatus.NameResolutionFailure:
							msg = LocalizationManager.GetString(
								"AndroidSynchronization.NameResolutionFailure",
								"HearThis could not make sense of the address you gave for the " +
								"device. Please try again.");
							break;
						case WebExceptionStatus.ConnectFailure:
							msg = LocalizationManager.GetString(
								"AndroidSynchronization.ConnectFailure",
								"HearThis could not connect to the device. Check to be sure the " +
								"devices are on the same WiFi network and that there is not a " +
								"firewall blocking things.");
							break;
						case WebExceptionStatus.ConnectionClosed:
							msg = LocalizationManager.GetString(
								"AndroidSynchronization.ConnectionClosed",
								"The connection to the device closed unexpectedly. Please don't " +
								"try to use the device for other things during the transfer. If the " +
								"device is going to sleep, you can change settings to prevent this.");
							break;
						default:
						{
							msg = ex.Response is HttpWebResponse response &&
								response.StatusCode == HttpStatusCode.RequestTimeout ? ex.Message :
									Format(LocalizationManager.GetString(
										"AndroidSynchronization.OtherWebException",
										"Something went wrong with the transfer. The system message is {0}. " +
										"Please try again, or report the problem if it keeps happening"),
									ex.Message);

							break;
						}
					}

					dlg.ProgressBox.WriteError(msg);
				}
			};
			dlg.Show(parent);
		}

		//private static IPAddress GetNetworkAddress(Form parent)
		//{
		//	// Retrieve all network interfaces
		//	var allOperationalNetworks = NetworkInterface.GetAllNetworkInterfaces()
		//		.Where(ni => ni.OperationalStatus == OperationalStatus.Up).ToArray();
		//
		//	if (!allOperationalNetworks.Any())
		//	{
		//		MessageBox.Show(parent, LocalizationManager.GetString("AndroidSynchronization.NetworkingRequired",
		//			"Android synchronization requires your computer to have networking enabled."),
		//			Program.kProduct);
		//		return null;
		//	}
		//
		//	(NetworkInterface Network, IPAddress Address)? preferred = null;
		//
		//	// WM, debug only!
		//	Debug.WriteLine("WM, AndroidSync.IPAddress, got network interfaces, now assess");
		//	foreach (var network in allOperationalNetworks)
		//	{
		//		var ipAddress = network.GetIPProperties().UnicastAddresses.Select(ip => ip.Address)
		//			.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
		//		Debug.WriteLine("  checking " + ipAddress);
		//
		//		if (ipAddress != null)
		//		{
		//			Debug.WriteLine("    NetworkInterfaceType = " + network.NetworkInterfaceType);
		//			if (network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
		//			{
		//				preferred = (network, ipAddress);
		//				Debug.WriteLine("  found WiFi interface at " + ipAddress + ", stop looking");
		//				break;
		//			}
		//
		//			if (preferred == null)
		//			{
		//				preferred = (network, ipAddress);
		//			}
		//		}
		//	}
		//
		//	if (!preferred.HasValue)
		//	{
		//		Debug.WriteLine("WM, AndroidSync.IPAddress, WiFi interface has no IP address, bail");
		//		MessageBox.Show(parent, LocalizationManager.GetString("AndroidSynchronization.NoInterNetworkIPAddress",
		//			"Sorry, your network adapter has no InterNetwork IP address. If you do not know how to resolve this, please seek technical help.",
		//			Program.kProduct));
		//		return null;
		//	}
		//
		//	Logger.WriteEvent("Found " +
		//		$"{(preferred.Value.Network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "a" : "only a wired")}" +
		//		$" network for Android synchronization: {preferred.Value.Network.Description}.");
		//
		//	Debug.WriteLine("WM, AndroidSync.IPAddress, returning " + preferred.Value.Address.ToString());
		//	return preferred.Value.Address;
		//}

		// We need *the* IP address of *this* (the local) machine. Since a machine running HearThis
		// can have multiple IP addresses (mine has 11, a mix of both IPv4 and IPv6), we must carefully
		// select the one that will actually be used by a network interface. Returning the first address
		// found of type 'AddressFamily.InterNetwork' that is also "Up" is sometimes NOT correct. So,
		// instead use this alternative function based on the post at
		// https://stackoverflow.com/questions/6803073/get-local-ip-address/27376368#27376368
		//
		private static string GetIpAddressOfNetworkIface()
		{
			IPEndPoint endpoint;
			Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
			sock.Connect("8.8.8.8", 65530); // Google's public DNS service
			endpoint = sock.LocalEndPoint as IPEndPoint;

			// Reconstitute the following here:
			//Logger.WriteEvent("Found " +
			//	$"{(preferred.Value.Network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "a" : "only a wired")}" +
			//	$" network for Android synchronization: {preferred.Value.Network.Description}.");

			Debug.WriteLine("WM, AndroidSync.GetIpAddressOfNetworkIface, returning " + endpoint.Address.ToString());
			return endpoint.Address.ToString();
		}

		// WM, debug only! Ported from temporary Bloom dev code: broadcast_address_buildup_test_02.cs
		// Unlike for BloomDesktop, we do not need to identify which network interface is being used
		// because we are not going to be transmitting on it to share our local IP. The QR code does
		// that instead.
		//
		private static void showNetworkInterfaces()
		{
			Debug.WriteLine("WM, AndroidSync.showNetworkInterfaces:");
			int i = -1;

			foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
			{
				Debug.WriteLine("  ----------");
				i++;  // doing it here lets an increment still follow a 'continue'
				Debug.WriteLine("  nic[" + i + "].Name = " + nic.Name);
				Debug.WriteLine("  nic[" + i + "].Id   = " + nic.Id);
				Debug.WriteLine("  nic[" + i + "].Description = " + nic.Description);
				Debug.WriteLine("  nic[" + i + "].NetworkInterfaceType = " + nic.NetworkInterfaceType);
				Debug.WriteLine("  nic[" + i + "].OperationalStatus = " + nic.OperationalStatus);
				Debug.WriteLine("  nic[" + i + "].Speed = " + nic.Speed);

				if (!nic.Supports(NetworkInterfaceComponent.IPv6))
				{
					Debug.WriteLine("    does not support IPv6"); // on my machine only Wi-Fi has this
				}
				if (!nic.Supports(NetworkInterfaceComponent.IPv4))
				{
					Debug.WriteLine("    does not support IPv4");
					continue;
				}

				IPInterfaceProperties ipProps = nic.GetIPProperties();
				IPv4InterfaceProperties ipPropsV4 = ipProps.GetIPv4Properties();
				if (ipPropsV4 == null)
				{
					Debug.WriteLine("    IPv4 information not available");
					continue;
				}

				// Show all IP addresses held by this network interface. Also show all
				// IPv4 netmasks. Note that IPv4 netmasks contain all zeroes for IPv6
				// addresses (which we don't use).
				foreach (UnicastIPAddressInformation addr in ipProps.UnicastAddresses)
				{
					Debug.WriteLine("  nic[" + i + "], ipProps.addr.Address  = " + addr.Address.ToString());
					Debug.WriteLine("  nic[" + i + "], ipProps.addr.IPv4Mask = " + addr.IPv4Mask);
				}
			}
			Debug.WriteLine("WM, AndroidSync.showNetworkInterfaces, finished");
		}
	}
}
