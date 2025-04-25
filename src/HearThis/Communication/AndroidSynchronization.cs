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
using System.Runtime.InteropServices;
using NDesk.DBus;

namespace HearThis.Communication
{
	/// <summary>
	/// This class encapsulates the logic around performing synchronization with Android devices.
	/// </summary>
	public static class AndroidSynchronization
	{
		private const string kHearThisAndroidProductName = "HearThis Android";

		// Layout of a row in the IPv4 routing table.
		[StructLayout(LayoutKind.Sequential)]
		public struct MIB_IPFORWARDROW
		{
			public uint dwForwardDest;
			public uint dwForwardMask;
			public uint dwForwardPolicy;
			public uint dwForwardNextHop;
			public int dwForwardIfIndex;
			public int dwForwardType;
			public int dwForwardProto;
			public int dwForwardAge;
			public int dwForwardNextHopAS;
			public int dwForwardMetric1;  // the "interface metric" we need
			public int dwForwardMetric2;
			public int dwForwardMetric3;
			public int dwForwardMetric4;
			public int dwForwardMetric5;
		}

		// Holds a copy of the IPv4 routing table, which we will examine
		// to find which row/route has the lowest "interface metric".
		[StructLayout(LayoutKind.Sequential)]
		private struct MIB_IPFORWARDTABLE
		{
			private int dwNumEntries;
			private MIB_IPFORWARDROW table;
		}

		// We use an unmanaged function in the C/C++ DLL "iphlpapi.dll".
		//   - "true": calling this function *can* set an error code,
		//     which will be retrieveable via Marshal.GetLastWin32Error()
		[DllImport("iphlpapi.dll", SetLastError = true)]
		static extern int GetIpForwardTable(IntPtr pIpForwardTable, ref int pdwSize, bool bOrder);

		// Holds relevant network interface attributes.
		private class InterfaceInfo
		{
			public string IpAddr  { get; set; }
			public string Type    { get; set; }
			public int Metric     { get; set; }
		}

		// Holds the current network interface candidate. After all interfaces are
		// checked this will hold the one having the lowest interface metric (the
		// "winner") which is the one Windows will choose for network traffic.
		static InterfaceInfo IfaceWinner = new InterfaceInfo();

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

			// Determine which interface the network stack will use, and save its key data in
			// the result object 'IfaceWinner'.
			GetInterfaceStackWillUse(parent);

			if (IfaceWinner.IpAddr == null)
			{
				Debug.WriteLine("AndroidSynchronization, local IP not found");
				return;
			}

			var address = IfaceWinner.IpAddr;
			Debug.WriteLine("AndroidSynchronization, local IP = {0} ({1})", IfaceWinner.IpAddr, IfaceWinner.Type);

			dlg.SetOurIpAddress(address.ToString());
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

		private static void GetInterfaceStackWillUse(Form parent)
		{
			int currentIfaceMetric;

			// Initialize result struct's metric field to the highest possible value
			// so the first interface metric value seen will always replace it.
			IfaceWinner.Metric = int.MaxValue;

			// Retrieve all network interfaces that are *active*.
			var allOperationalNetworks = NetworkInterface.GetAllNetworkInterfaces()
				.Where(ni => ni.OperationalStatus == OperationalStatus.Up).ToArray();

			if (!allOperationalNetworks.Any())
			{
				MessageBox.Show(parent, LocalizationManager.GetString("AndroidSynchronization.NetworkingRequired",
					"Android synchronization requires your computer to have networking enabled."),
					Program.kProduct);
				Debug.WriteLine("AndroidSynchronization, no network interfaces are operational");
				return;
			}

			// Get key attributes of active network interfaces.
			foreach (NetworkInterface ni in allOperationalNetworks)
			{
				// If we can't get IP or IPv4 properties for this interface, skip it.
				var ipProps = ni.GetIPProperties();
				if (ipProps == null)
				{
					continue;
				}
				var ipv4Props = ipProps.GetIPv4Properties();
				if (ipv4Props == null)
				{
					continue;
				}

				//Debug.WriteLine("AndroidSynchronization, checking IP addresses in " + ni.Name);
				foreach (UnicastIPAddressInformation ip in ipProps.UnicastAddresses)
				{
					// We don't consider IPv6 so filter for IPv4 ('InterNetwork')
					if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
					{
						currentIfaceMetric = GetMetricForInterface(ipv4Props.Index);

						// If this interface's metric is lower than what we've seen
						// so far, save it and its relevant associated values.
						if (currentIfaceMetric < IfaceWinner.Metric)
						{
							IfaceWinner.IpAddr = ip.Address.ToString();
							IfaceWinner.Type = ni.NetworkInterfaceType.ToString();
							IfaceWinner.Metric = currentIfaceMetric;
						}
					}
				}
			}
		}

		// Get a key piece of info ("metric") from the specified network interface.
		// https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getipforwardtable
		//
		// Retrieving the metric is not as simple as grabbing one of the fields in
		// the network interface. The metric resides in the network stack routing
		// table. One of the interface fields ("Index") is also in the routing table
		// and is how we correlate the two.
		//   - Calling code (walking the interface collection) passes in the index
		//     of the interface whose "best" metric it wants.
		//   - This function walks the routing table looking for all rows (each of
		//     which is a route) containing that index. It notes the metric in each
		//     route and returns the lowest among all routes/rows for the interface.
		//
		private static int GetMetricForInterface(int interfaceIndex)
		{
			int bestMetric;

			// Preliminary: call with a null buffer ('size') to learn how large a
			// buffer is needed to hold a copy of the routing table.
			int size = 0;
			GetIpForwardTable(IntPtr.Zero, ref size, false);

			// 'size' now shows how large a buffer is needed, so allocate it.
			IntPtr tableBuf = Marshal.AllocHGlobal(size);

			try
			{
				// Copy the routing table into buffer for examination.
				// If the result code is not 0 then something went wrong.
				int error = GetIpForwardTable(tableBuf, ref size, false);
				if (error != 0)
				{
					// It is tempting to add a dealloc call here before bailing, but
					// don't. The dealloc in the finally-block *will* be done (I checked).
					Console.WriteLine("  GetMetricForInterface, ERROR, GetIpForwardTable() = {0}, returning {1}", error, int.MaxValue);
					return int.MaxValue;
				}

				// Get number of routing table entries.
				int numEntries = Marshal.ReadInt32(tableBuf);

				// Advance pointer past the integer to point at 1st row.
				IntPtr rowPtr = IntPtr.Add(tableBuf, 4);

				// Initialize to "worst" possible metric (Win10 Pro: 2^31 - 1).
				// It can only get better from there!
				bestMetric = int.MaxValue;

				// Walk the routing table looking for rows involving the the network
				// interface passed in. For each such row/route, check the metric.
				// If it is lower than the lowest we've yet seen, save it to become
				// the new benchmark.
				for (int i = 0; i < numEntries; i++)
				{
					MIB_IPFORWARDROW row = Marshal.PtrToStructure<MIB_IPFORWARDROW>(rowPtr);
					if (row.dwForwardIfIndex == interfaceIndex)
					{
						bestMetric = Math.Min(bestMetric, row.dwForwardMetric1);
					}
					rowPtr = IntPtr.Add(rowPtr, Marshal.SizeOf<MIB_IPFORWARDROW>());
				}
			}
			finally
			{
				Marshal.FreeHGlobal(tableBuf);
			}
			return bestMetric;
		}
	}
}
