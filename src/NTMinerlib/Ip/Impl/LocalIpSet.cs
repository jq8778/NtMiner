﻿using NTMiner.MinerClient;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;

namespace NTMiner.Ip.Impl {
    public class LocalIpSet : ILocalIpSet {
        #region private static method GetManagementObject
        private static ManagementObject GetManagementObject(string settingId) {
            using (ManagementClass mc = new ManagementClass("Win32_NetworkAdapterConfiguration")) {
                ManagementObjectCollection moc = mc.GetInstances();
                foreach (ManagementObject mo in moc) {
                    if ((string)mo["SettingID"] == settingId) {
                        return mo;
                    }
                }
            }
            return null;
        }
        #endregion

        #region private static method GetLocalIps
        private static List<LocalIpData> GetLocalIps() {
            List<LocalIpData> list = new List<LocalIpData>();
            try {
                using (ManagementClass mc = new ManagementClass("Win32_NetworkAdapterConfiguration")) {
                    ManagementObjectCollection moc = mc.GetInstances();
                    foreach (ManagementObject mo in moc) {
                        if (!(bool)mo["IPEnabled"] || mo["DefaultIPGateway"] == null) {
                            continue;
                        }
                        string[] defaultIpGateways = (string[])mo["DefaultIPGateway"];
                        if (defaultIpGateways.Length == 0) {
                            continue;
                        }
                        string dNSServer0 = string.Empty;
                        string dNSServer1 = string.Empty;
                        if (mo["DNSServerSearchOrder"] != null) {
                            string[] dNSServerSearchOrder = (string[])mo["DNSServerSearchOrder"];
                            if (dNSServerSearchOrder.Length > 0) {
                                if (dNSServerSearchOrder[0] != defaultIpGateways[0]) {
                                    dNSServer0 = dNSServerSearchOrder[0];
                                }
                            }
                            if (dNSServerSearchOrder.Length > 1) {
                                dNSServer1 = dNSServerSearchOrder[1];
                            }
                        }
                        string ipAddress = string.Empty;
                        if (mo["IPAddress"] != null) {
                            string[] items = (string[])mo["IPAddress"];
                            if (items.Length != 0) {
                                ipAddress = items[0];// 只取Ipv4
                            }
                        }
                        string ipSubnet = string.Empty;
                        if (mo["IPSubnet"] != null) {
                            string[] items = (string[])mo["IPSubnet"];
                            if (items.Length != 0) {
                                ipSubnet = items[0];// 只取Ipv4
                            }
                        }
                        list.Add(new LocalIpData {
                            DefaultIPGateway = defaultIpGateways[0],
                            DHCPEnabled = (bool)mo["DHCPEnabled"],
                            SettingID = (string)mo["SettingID"],
                            IPSubnet = ipSubnet,
                            DNSServer0 = dNSServer0,
                            DNSServer1 = dNSServer1,
                            IPAddress = ipAddress
                        });
                        FillNames(list);
                    }
                }
            }
            catch (Exception e) {
                Logger.ErrorDebugLine(e);
            }
            return list;
        }
        #endregion

        private List<LocalIpData> _localIps = new List<LocalIpData>();
        public LocalIpSet() {
            NetworkChange.NetworkAddressChanged += (object sender, EventArgs e) => {
                // 延迟获取网络信息以防止立即获取时获取不到
                TimeSpan.FromSeconds(1).Delay().ContinueWith(t => {
                    var old = _localIps;
                    _isInited = false;
                    InitOnece();
                    var localIps = _localIps;
                    if (localIps.Count == 0) {
                        VirtualRoot.ThisLocalWarn(nameof(LocalIpSet), "网络连接已断开", toConsole: true);
                    }
                    else {
                        if (old.Count == 0) {
                            VirtualRoot.ThisLocalInfo(nameof(LocalIpSet), "网络连接已连接", toConsole: true);
                        }
                        else {
                            bool isIpChanged = false;
                            if (old.Count != localIps.Count) {
                                isIpChanged = true;
                            }
                            else {
                                foreach (var item in localIps) {
                                    var oldItem = old.FirstOrDefault(a => a.SettingID == item.SettingID);
                                    if (item != oldItem) {
                                        isIpChanged = true;
                                        break;
                                    }
                                }
                            }
                            VirtualRoot.ThisLocalWarn(nameof(LocalIpSet), $"网络接口的 IP 地址发生了 {(isIpChanged ? "变更" : "刷新")}", toConsole: true);
                        }
                    }
                    VirtualRoot.RaiseEvent(new LocalIpSetRefreshedEvent());
                });
            };
            NetworkChange.NetworkAvailabilityChanged += (object sender, NetworkAvailabilityEventArgs e) => {
                if (e.IsAvailable) {
                    VirtualRoot.ThisLocalInfo(nameof(LocalIpSet), $"网络可用", toConsole: true);
                }
                else {
                    VirtualRoot.ThisLocalWarn(nameof(LocalIpSet), $"网络不可用", toConsole: true);
                }
            };
            VirtualRoot.BuildCmdPath<SetLocalIpCommand>(action: message => {
                ManagementObject mo = GetManagementObject(message.Input.SettingID);
                if (mo != null) {
                    if (message.Input.DHCPEnabled) {
                        mo.InvokeMethod("EnableStatic", null);
                        mo.InvokeMethod("SetGateways", null);
                        mo.InvokeMethod("EnableDHCP", null);
                    }
                    else {
                        ManagementBaseObject inPar = mo.GetMethodParameters("EnableStatic");
                        inPar["IPAddress"] = new string[] { message.Input.IPAddress };
                        inPar["SubnetMask"] = new string[] { message.Input.IPSubnet };
                        mo.InvokeMethod("EnableStatic", inPar, null);
                        inPar = mo.GetMethodParameters("SetGateways");
                        inPar["DefaultIPGateway"] = new string[] { message.Input.DefaultIPGateway };
                        mo.InvokeMethod("SetGateways", inPar, null);
                    }

                    if (message.IsAutoDNSServer) {
                        mo.InvokeMethod("SetDNSServerSearchOrder", null);
                    }
                    else {
                        ManagementBaseObject inPar = mo.GetMethodParameters("SetDNSServerSearchOrder");
                        inPar["DNSServerSearchOrder"] = new string[] { message.Input.DNSServer0, message.Input.DNSServer1 };
                        mo.InvokeMethod("SetDNSServerSearchOrder", inPar, null);
                    }
                }
            });
        }

        private bool _isInited = false;
        private readonly object _locker = new object();

        private void InitOnece() {
            if (_isInited) {
                return;
            }
            Init();
        }

        private void Init() {
            lock (_locker) {
                if (!_isInited) {
                    _localIps = GetLocalIps();
                    _isInited = true;
                }
            }
        }

        private static void FillNames(List<LocalIpData> list) {
            //获取网卡
            var items = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var item in list) {
                foreach (NetworkInterface ni in items) {
                    if (ni.Id == item.SettingID) {
                        item.Name = ni.Name;
                    }
                }
            }
        }

        public IEnumerable<ILocalIp> AsEnumerable() {
            InitOnece();
            return _localIps;
        }
    }
}
