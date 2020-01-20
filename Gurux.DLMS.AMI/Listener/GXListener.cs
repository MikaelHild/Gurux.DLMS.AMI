﻿//
// --------------------------------------------------------------------------
//  Gurux Ltd
//
//
//
// Filename:        $HeadURL$
//
// Version:         $Revision$,
//                  $Date$
//                  $Author$
//
// Copyright (c) Gurux Ltd
//
//---------------------------------------------------------------------------
//
//  DESCRIPTION
//
// This file is a part of Gurux Device Framework.
//
// Gurux Device Framework is Open Source software; you can redistribute it
// and/or modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; version 2 of the License.
// Gurux Device Framework is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// This code is licensed under the GNU General Public License v2.
// Full text may be retrieved at http://www.gnu.org/licenses/gpl-2.0.txt
//---------------------------------------------------------------------------

using Gurux.Common;
using Gurux.DLMS.AMI.Internal;
using Gurux.DLMS.AMI.Messages.DB;
using Gurux.DLMS.AMI.Messages.Enums;
using Gurux.DLMS.AMI.Messages.Rest;
using Gurux.DLMS.AMI.Reader;
using Gurux.DLMS.Enums;
using Gurux.DLMS.Objects;
using Gurux.DLMS.Secure;
using Gurux.Net;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Gurux.DLMS.AMI.Notify
{
    /// <summary>
    /// Listerner is used with dynamic IP addresses.
    /// </summary>
    public class GXListener
    {
        /// <summary>
        /// Client has made a connection to the server.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        internal static void OnClientConnected(object sender, Gurux.Common.ConnectionEventArgs e)
        {
            Console.WriteLine("Client {0} is connected.", e.Info);
            GXNet server = (GXNet)sender;
            try
            {
                GXNet media = server.Attach(e.Info);
                Thread thread = new Thread(new ParameterizedThreadStart(ReadMeter));
                thread.Start(media);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        /// <summary>
        /// Read data from the meter.
        /// </summary>
        private static void ReadMeter(object parameter)
        {
            GXDLMSReader reader = null;
            try
            {
                var config = new ConfigurationBuilder()
                   .SetBasePath(Directory.GetCurrentDirectory())
                   .AddJsonFile("appsettings.json", optional: true)
                   .Build();
                ListenerOptions listener = config.GetSection("Listener").Get<ListenerOptions>();

                System.Net.Http.HttpClient httpClient = new System.Net.Http.HttpClient();
                using (GXNet media = (GXNet)parameter)
                {
                    GXDLMSObjectCollection objects = new GXDLMSObjectCollection();
                    GXDLMSSecureClient client = new GXDLMSSecureClient(listener.UseLogicalNameReferencing, listener.ClientAddress, listener.ServerAddress, (Authentication)listener.Authentication, listener.Password, (InterfaceType)listener.Interface);
                    reader = new GXDLMSReader(client, media, TraceLevel.Verbose, null);
                    GXDLMSData ldn = new GXDLMSData("0.0.42.0.0.255");
                    ldn.SetUIDataType(2, DataType.String);
                    reader.InitializeConnection();
                    reader.Read(ldn, 2);
                    Console.WriteLine("Meter connected: " + ldn.Value);
                    //Find device.
                    GXDevice dev = null;
                    ListDevicesResponse devs = null;
                    using (System.Net.Http.HttpClient cl = new System.Net.Http.HttpClient())
                    {
                        using (System.Net.Http.HttpResponseMessage response = cl.PostAsJsonAsync(Startup.ServerAddress + "/api/device/ListDevices", new ListDevices() { Name = (string)ldn.Value }).Result)
                        {
                            Helpers.CheckStatus(response);
                            devs = response.Content.ReadAsAsync<ListDevicesResponse>().Result;
                        }
                        //If device is unknown.
                        if (devs.Devices.Length == 0)
                        {
                            if (listener.DefaultDeviceTemplate == 0)
                            {
                                string str = "Unknown Meter try to connect to the Gurux.DLMS.AMI server: " + ldn.Value;
                                Console.WriteLine(str);
                                AddSystemError info = new AddSystemError();
                                info.Error = new GXSystemError()
                                {
                                    Error = str
                                };
                                using (System.Net.Http.HttpResponseMessage response = cl.PostAsJsonAsync(Startup.ServerAddress + "/api/SystemError/AddSystemError", info).Result)
                                {
                                    Helpers.CheckStatus(response);
                                }
                                if (reader != null)
                                {
                                    reader.Close();
                                }
                                return;
                            }
                            ListDeviceTemplates lt = new ListDeviceTemplates() { Ids = new UInt64[] { listener.DefaultDeviceTemplate } };
                            using (System.Net.Http.HttpResponseMessage response = cl.PostAsJsonAsync(Startup.ServerAddress + "/api/template/ListDeviceTemplates", lt).Result)
                            {
                                Helpers.CheckStatus(response);
                                ListDeviceTemplatesResponse ret = response.Content.ReadAsAsync<ListDeviceTemplatesResponse>().Result;
                                if (ret.Devices.Length != 1)
                                {
                                    throw new Exception("DefaultDeviceTemplate value is invalid: " + listener.DefaultDeviceTemplate);
                                }
                                dev = new GXDevice();
                                GXDevice.Copy(dev, ret.Devices[0]);
                                dev.Name = Convert.ToString(ldn.Value);
                                dev.TemplateId = listener.DefaultDeviceTemplate;
                                dev.Manufacturer = ret.Devices[0].Name;
                            }
                            dev.Dynamic = true;
                            UpdateDevice update = new UpdateDevice();
                            update.Device = dev;
                            using (System.Net.Http.HttpResponseMessage response = cl.PostAsJsonAsync(Startup.ServerAddress + "/api/device/UpdateDevice", update).Result)
                            {
                                Helpers.CheckStatus(response);
                                UpdateDeviceResponse r = response.Content.ReadAsAsync<UpdateDeviceResponse>().Result;
                                dev.Id = r.DeviceId;
                            }
                            using (System.Net.Http.HttpResponseMessage response = cl.PostAsJsonAsync(Startup.ServerAddress + "/api/device/ListDevices", new ListDevices() { Ids = new UInt64[] { dev.Id } }).Result)
                            {
                                Helpers.CheckStatus(response);
                                devs = response.Content.ReadAsAsync<ListDevicesResponse>().Result;
                            }
                        }
                        if (devs.Devices.Length != 1)
                        {
                            throw new Exception("There are multiple devices with same name: " + ldn.Value);
                        }
                        else
                        {
                            dev = devs.Devices[0];
                            if (dev.Security != Security.None)
                            {
                                Console.WriteLine("Reading frame counter.");
                                GXDLMSData fc = new GXDLMSData(listener.InvocationCounter);
                                reader.Read(fc, 2);
                                dev.InvocationCounter = 1 + Convert.ToUInt32(fc.Value);
                                Console.WriteLine("Device ID: " + dev.Id + " LDN: " + (string)ldn.Value);
                                Console.WriteLine("Frame counter: " + dev.FrameCounter);
                            }
                            GetNextTaskResponse ret;
                            using (System.Net.Http.HttpResponseMessage response = cl.PostAsJsonAsync(Startup.ServerAddress + "/api/task/GetNextTask", new GetNextTask() { Listener = true, DeviceId = dev.Id }).Result)
                            {
                                Helpers.CheckStatus(response);
                                ret = response.Content.ReadAsAsync<GetNextTaskResponse>().Result;
                            }
                            if (ret.Tasks == null || ret.Tasks.Length == 0)
                            {
                                Console.WriteLine("No tasks to execute");
                            }
                            else
                            {
                                Console.WriteLine("Task count: " + ret.Tasks.Length);
                                if (client.ClientAddress != dev.ClientAddress || dev.Security != Security.None)
                                {
                                    reader.Release();
                                    client = new GXDLMSSecureClient(dev.UseLogicalNameReferencing, dev.ClientAddress, dev.PhysicalAddress, (Authentication)dev.Authentication, dev.Password, dev.InterfaceType);
                                    client.UtcTimeZone = dev.UtcTimeZone;
                                    client.Standard = (Standard)dev.Standard;
                                    if (dev.Conformance != 0)
                                    {
                                        client.ProposedConformance = (Conformance)dev.Conformance;
                                    }
                                    client.Priority = dev.Priority;
                                    client.ServiceClass = dev.ServiceClass;
                                    client.Ciphering.SystemTitle = GXCommon.HexToBytes(dev.ClientSystemTitle);
                                    client.Ciphering.BlockCipherKey = GXCommon.HexToBytes(dev.BlockCipherKey);
                                    client.Ciphering.AuthenticationKey = GXCommon.HexToBytes(dev.AuthenticationKey);
                                    client.ServerSystemTitle = GXCommon.HexToBytes(dev.DeviceSystemTitle);
                                    client.Ciphering.InvocationCounter = dev.InvocationCounter;
                                    client.Ciphering.Security = (Security)dev.Security;
                                    reader = new GXDLMSReader(client, media, TraceLevel.Verbose, null);
                                    reader.InitializeConnection();
                                }
                                List<GXValue> values = new List<GXValue>();
                                foreach (GXTask task in ret.Tasks)
                                {
                                    GXDLMSObject obj = GXDLMSClient.CreateObject((ObjectType)task.Object.ObjectType);
                                    obj.LogicalName = task.Object.LogicalName;
                                    try
                                    {
                                        if (task.TaskType == TaskType.Write)
                                        {
                                            if (obj.LogicalName == "0.0.1.1.0.255" && task.Index == 2)
                                            {
                                                client.UpdateValue(obj, task.Index, GXDateTime.ToUnixTime(DateTime.UtcNow));
                                            }
                                            else
                                            {
                                                client.UpdateValue(obj, task.Index, GXDLMSTranslator.XmlToValue(task.Data));
                                            }
                                            reader.Write(obj, task.Index);
                                        }
                                        else if (task.TaskType == TaskType.Action)
                                        {
                                            reader.Method(obj, task.Index, GXDLMSTranslator.XmlToValue(task.Data), DataType.None);
                                        }
                                        else if (task.TaskType == TaskType.Read)
                                        {
                                            Reader.Reader.Read(null, httpClient, reader, task, media, obj);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        task.Result = ex.Message;
                                        AddError error = new AddError();
                                        error.Error = new GXError()
                                        {
                                            DeviceId = dev.Id,
                                            Error = "Failed to " + task.TaskType + " " + task.Object.LogicalName + ":" + task.Index + ". " + ex.Message
                                        };
                                        using (System.Net.Http.HttpResponseMessage response = httpClient.PostAsJsonAsync(Startup.ServerAddress + "/api/error/AddError", error).Result)
                                        {
                                            Helpers.CheckStatus(response);
                                            response.Content.ReadAsAsync<AddErrorResponse>();
                                        }
                                    }
                                    using (System.Net.Http.HttpResponseMessage response = cl.PostAsJsonAsync(Startup.ServerAddress + "/api/task/TaskReady", new TaskReady() { Tasks = new GXTask[] { task } }).Result)
                                    {
                                        Helpers.CheckStatus(response);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            finally
            {
                if (reader != null)
                {
                    reader.Close();
                }
            }
        }
    }
}