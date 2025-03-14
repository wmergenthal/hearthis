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
using System.Collections.Generic;
using System.Drawing;

namespace HearThis.Communication
{
	/// <summary>
	/// This class encapsulates the logic around performing synchronization with Android devices.
	/// </summary>
	public static class AndroidSynchronization
	{
		private const string kHearThisAndroidProductName = "HearThis Android";

		private static List<string> IfIpAddresses = new List<string>();
		private static List<string> IfTypes = new List<string>();

		public static void DoAndroidSync(Project project, Form parent)
		{
			string localIp = "";

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

			// WM, DEBUG ONLY
			// Current local IP determination logic does consider network interfaces but it
			// does not always settle on the correct one. My Windows 10 laptop is an example.
			//
			// Instructive exercise: uncomment and run this debug function, ShowNetworkInterfaces(),
			// to list all network interfaces along with significant attributes of each including IP
			// addresses (an interface can have several).
			ShowNetworkInterfaces();
			// WM, END DEBUG

			// Get the local IP address we will present to Android devices.
			localIp = GetIpAddressOfNetworkIface();
			if (localIp.Length == 0)
			{
				Debug.WriteLine("AndroidSynchronization: ERROR, can't get local IP address, exiting");
				return;
			}

			// Bake the local IP into a QR code and display it for an Android to scan.
			dlg.SetOurIpAddress(localIp);
			dlg.ShowAndroidIpAddress(); // AFTER we set our IP address, which may be used to provide a default
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
					RobustFile.WriteAllText(infoFilePath, project.GetProjectRecordingStatusInfoFileContent());
					var theirInfoTxtPath = project.Name + "/" + Project.InfoTxtFileName;
					theirLink.PutFile(theirInfoTxtPath, File.ReadAllBytes(infoFilePath));
					theirLink.SendNotification("syncCompleted");
					dlg.ProgressBox.WriteMessage("Sync completed successfully");
					//dlg.Close();
				}
				catch (WebException ex)
				{
					string msg;
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

		// To find which local IP address to present to Android devices we employ the following
		// process. Wi-Fi is preferred but a wired connection - our 2nd choice - may also work if
		// the network is configured to allow it.
		//
		//   1. Examine all network interfaces. For those that support IPv4 and are active:
		//      1.1 Save (in lists) the IPv4 address and the Type.
		//   2. Find the local IP address that .NET/Windows would use for network traffic.
		//   3. Decide which IP address from Step 1's list to return:
		//      3.1 If the list has a *Wi-Fi* address that matches Step 2 (expect this nearly always),
		//          CHOOSE THIS ONE.
		//      3.2 Else if the list has an *Ethernet* address matching Step 2 (fairly common),
		//          CHOOSE THIS ONE.
		//      3.3 Else if the list has a *Wi-Fi* address that does not match Step 2 (expect this seldom),
		//          CHOOSE THIS ONE WITH WARNING.
		//      3.4 Else we got nothing. Put up dialog to allow user to manually enter IP address as is
		//          currently done BUT have them enter the entire address, not just the final octet.
		//
		// Note for Step 2: a Windows machine can have multiple IP addresses (mine has 11, a
		// mix of both IPv4 and IPv6). To learn which one would actually be used for network
		// traffic we use the technique described at
		// https://stackoverflow.com/questions/6803073/get-local-ip-address/27376368#27376368
		//
		private static string GetIpAddressOfNetworkIface()
		{
			string localIp = "";

			// Step 1
			GetNetworkInterfaceInfo(IfIpAddresses, IfTypes);
			Debug.WriteLine("AndroidSynchronization, Step 1 done");

			// Step 2
			IPEndPoint endpoint;
			Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
			sock.Connect("8.8.8.8", 65530); // Google's public DNS service
			endpoint = sock.LocalEndPoint as IPEndPoint;
			string ipWouldBeUsed = endpoint.Address.ToString();
			Debug.WriteLine("AndroidSynchronization, Step 2 done, Windows would use IP = " + ipWouldBeUsed);

			// Step 3.1
			localIp = GetInterfaceIpAddress("Wireless80211", ipWouldBeUsed);
			Debug.WriteLine("AndroidSynchronization, Step 3.1 done, so far = " + localIp);

			// Step 3.2
			if (localIp == "")
			{
				localIp = GetInterfaceIpAddress("Ethernet", ipWouldBeUsed);
				Debug.WriteLine("AndroidSynchronization, Step 3.2 done, so far = " + localIp);
			}

			// Step 3.3
			if (localIp == "")
			{
				localIp = GetInterfaceIpAddress("Wireless80211", "any");
				Debug.WriteLine("AndroidSynchronization, Step 3.3 done, so far = " + localIp);
			}

			// Step 3.4
			// If 'localIp' is still empty then give up.

			// TODO *********************
			//	Logger.WriteEvent("Found " +
			//		$"{(preferred.Value.Network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "a" : "only a wired")}" +
			//		$" network for Android synchronization: {preferred.Value.Network.Description}.");

			Debug.WriteLine("AndroidSynchronization, done, returning local IP = " + localIp);
			return localIp;
		}

		// Function to gather info on all *active* network interfaces of this machine.
		//
		private static void GetNetworkInterfaceInfo(List<string> ifAddressList, List<string> ifTypeList)
		{
			// index i: initialize to 1 less than usual so its increment can occur at the
			//          top of a loop, and thus not get missed after a 'continue'
			// index j: initialize normally
			int i = -1;
			int j = 0;

			Debug.WriteLine("AndroidSynchronization, GetNetworkInterfaceInfo: begin");
			foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
			{
				i++;

				Debug.WriteLine("  nic[" + i + "].Name = " + nic.Name);  // WM, TEMPORARY ONLY!
				if (nic.OperationalStatus.ToString() != "Up")
				{
					Debug.WriteLine("  nic[" + i + "], interface not active");
					continue;  // this i/f is not active, skip it
				}

				if (!nic.Supports(NetworkInterfaceComponent.IPv4))
				{
					Debug.WriteLine("  NOTE: nic[" + i + "], IPv4 not supported");
					continue;  // IPv4 not supported, skip this i/f
				}

				IPInterfaceProperties ipProps = nic.GetIPProperties();
				if (ipProps == null)
				{
					Debug.WriteLine("  NOTE: nic[" + i + "], IP information not available");
					continue;  // IP information not available, skip this i/f
				}
				IPv4InterfaceProperties ipPropsV4 = ipProps.GetIPv4Properties();
				if (ipPropsV4 == null)
				{
					Debug.WriteLine("  NOTE: nic[" + i + "], IPv4 information not available");
					continue;  // IPv4 information not available, skip this i/f
				}

				// This network i/f is of interest. We want to know:
				//   - its IP addresses (IPv4 only)
				//   - its type
				foreach (UnicastIPAddressInformation addr in ipProps.UnicastAddresses)
				{
					string candidate = addr.Address.ToString();
					Debug.WriteLine("    nic[" + i + "].IP = " + candidate);  // WM, TEMPORARY ONLY!

					if (!candidate.Contains(":"))   // skip IPv6 addresses
					{
						ifAddressList.Add(candidate);
						ifTypeList.Add(nic.NetworkInterfaceType.ToString());

						Debug.WriteLine("    i={0} j={1} add to Active list: IP={2}, type={3}", i, j, candidate, nic.NetworkInterfaceType.ToString());
						j++;
					}
				}
				j = 0;
			}
			DumpNetworkInterfaceLists();  // WM, TEMP ONLY!
			Debug.WriteLine("AndroidSynchronization, GetNetworkInterfaceInfo: i={0}, DONE", i);
		}

		// Search the gathered network interface data for an IP address fulfilling the criteria:
		//   - argument 1 specifies the network interface type to which it should belong
		//   - argument 2 specifies the Windows-proposed IP address that it should match
		//
		private static string GetInterfaceIpAddress(string requestedType, string ipWouldBeUsed)
		{
			int index = 0;
			string foundIp = "";

			foreach (string type in IfTypes)
			{
				if (type == requestedType)
				{
					Debug.WriteLine("GetInterfaceIpAddress, type: {0} at index {1}", type, index);
					// Network interface types and IP addresses were saved in parallel lists
					// at the same index level.
					if (IfIpAddresses[index] == ipWouldBeUsed)
					{
						Debug.WriteLine("GetInterfaceIpAddress, IP: {0} at index {1}", ipWouldBeUsed, index);
						foundIp = ipWouldBeUsed;
						break;
					}
					else if (ipWouldBeUsed == "any")
					{
						Debug.WriteLine("GetInterfaceIpAddress, IP: {0} at index {1}, any", IfIpAddresses[index], index);
						foundIp = IfIpAddresses[index];
						break;
					}
				}
				index++;
			}

			// WM, DEBUG ONLY
			if (foundIp == "")
			{
				// No network interface of the requested type matches what Windows would use.
				Debug.WriteLine("GetInterfaceIpAddress, IP not found for " + requestedType + ", returning empty string");
			}
			else
			{
				Debug.WriteLine("GetInterfaceIpAddress, returning " + foundIp + " from index = " + index);
			}
			// WM, END DEBUG

			return foundIp;
		}

		// WM, DEBUG ONLY
		// Ported from temporary Bloom dev code: broadcast_address_buildup_test_02.cs
		// Unlike for BloomDesktop, HearThis doesn't need to identify which network interface is being
		// used because HearThis won't be transmitting on it to share its local IP. The QR code does
		// that instead. Still, it is interesting (and possibly helpful) to see the available network
		// interfaces and their main attributes.
		//
		private static void ShowNetworkInterfaces()
		{
			Debug.WriteLine("AndroidSynchronization, ShowNetworkInterfaces:");

			// index i: initialize to 1 less than usual so its increment can occur at the
			//          top of a loop, and thus not get missed after a 'continue'
			int i = -1;

			foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
			{
				Debug.WriteLine("  ----------");
				i++;
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
					Debug.WriteLine("    does not support IPv4"); // on my machine none show this
					continue;
				}

				IPInterfaceProperties ipProps = nic.GetIPProperties();
				IPv4InterfaceProperties ipPropsV4 = ipProps.GetIPv4Properties();
				if (ipPropsV4 == null)
				{
					Debug.WriteLine("    IPv4 information not available"); // on my machine none show this
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
			Debug.WriteLine("AndroidSynchronization, ShowNetworkInterfaces: DONE");
		}
		// WM, END DEBUG

		// WM, DEBUG ONLY
		private static void DumpNetworkInterfaceLists()
		{
			Debug.WriteLine("AndroidSynchronization, dump active lists, begin");
			int i = 0;
			foreach (string ip in IfIpAddresses)
			{
				Debug.WriteLine("  IfIpAddresses[" + i + "], IP = " + ip);
				i++;
			}

			i = 0;
			foreach (string type in IfTypes)
			{
				Debug.WriteLine("  IfTypes[" + i++ + "], type = " + type);
			}
			Debug.WriteLine("AndroidSynchronization, dump active lists, DONE");
		}
		// WM, END DEBUG
	}
}
