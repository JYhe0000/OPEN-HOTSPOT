using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices.ComTypes;

namespace OpenHotspot
{
    public sealed class IcsConnection
    {
        public string Name { get; set; }
        public bool SharingEnabled { get; set; }
        public int SharingType { get; set; }
        internal object ConfigObject { get; set; }
    }

    public sealed class IcsManager
    {
        private readonly object _manager;

        public IcsManager()
        {
            Type managerType = Type.GetTypeFromProgID("HNetCfg.HNetShare");
            if (managerType == null)
            {
                throw new InvalidOperationException("系统未安装 Internet 连接共享组件（HNetCfg.HNetShare）。");
            }
            _manager = Activator.CreateInstance(managerType);
        }

        public List<IcsConnection> GetConnections()
        {
            List<IcsConnection> result = new List<IcsConnection>();
            object collection = GetProperty(_manager, "EnumEveryConnection", null);
            if (collection == null) return result;

            foreach (object connection in Enumerate(collection))
            {
                IcsConnection info = new IcsConnection();
                info.Name = "";

                object props = GetProperty(_manager, "NetConnectionProps", new object[] { connection });
                if (props != null)
                {
                    object name = GetProperty(props, "Name", null);
                    if (name != null) info.Name = Convert.ToString(name);
                }

                object config = GetProperty(_manager, "INetSharingConfigurationForINetConnection", new object[] { connection });
                info.ConfigObject = config;

                if (config != null)
                {
                    try { info.SharingEnabled = Convert.ToBoolean(GetProperty(config, "SharingEnabled", null)); }
                    catch { info.SharingEnabled = false; }

                    try { info.SharingType = Convert.ToInt32(GetProperty(config, "SharingType", null)); }
                    catch { info.SharingType = -1; }
                }
                else
                {
                    info.SharingEnabled = false;
                    info.SharingType = -1;
                }

                result.Add(info);
            }
            return result;
        }

        public void EnableSharing(IcsConnection connection, int sharingType)
        {
            if (connection == null || connection.ConfigObject == null)
            {
                throw new ArgumentNullException("connection");
            }
            connection.ConfigObject.GetType().InvokeMember(
                "EnableSharing",
                BindingFlags.InvokeMethod,
                null,
                connection.ConfigObject,
                new object[] { sharingType });
        }

        public void DisableSharing(IcsConnection connection)
        {
            if (connection == null || connection.ConfigObject == null) return;
            connection.ConfigObject.GetType().InvokeMember(
                "DisableSharing",
                BindingFlags.InvokeMethod,
                null,
                connection.ConfigObject,
                new object[0]);
        }

        private static object GetProperty(object target, string name, object[] arguments)
        {
            return target.GetType().InvokeMember(
                name,
                BindingFlags.GetProperty,
                null,
                target,
                arguments);
        }

        private static List<object> Enumerate(object collection)
        {
            List<object> items = new List<object>();

            IEnumerable enumerable = collection as IEnumerable;
            if (enumerable != null)
            {
                foreach (object item in enumerable) items.Add(item);
                return items;
            }

            object newEnum = null;
            try
            {
                newEnum = collection.GetType().InvokeMember(
                    "_NewEnum",
                    BindingFlags.GetProperty,
                    null,
                    collection,
                    null);
            }
            catch
            {
            }

            IEnumVARIANT variant = newEnum as IEnumVARIANT;
            if (variant != null)
            {
                object[] buffer = new object[1];
                while (variant.Next(1, buffer, IntPtr.Zero) == 0)
                {
                    items.Add(buffer[0]);
                    buffer[0] = null;
                }
                return items;
            }

            try
            {
                object enumerator = collection.GetType().InvokeMember(
                    "GetEnumerator",
                    BindingFlags.InvokeMethod,
                    null,
                    collection,
                    null);
                if (enumerator != null)
                {
                    Type enumeratorType = enumerator.GetType();
                    MethodInfo moveNext = enumeratorType.GetMethod("MoveNext");
                    PropertyInfo current = enumeratorType.GetProperty("Current");
                    if (moveNext != null && current != null)
                    {
                        while (Convert.ToBoolean(moveNext.Invoke(enumerator, null)))
                        {
                            items.Add(current.GetValue(enumerator, null));
                        }
                    }
                }
            }
            catch
            {
            }

            return items;
        }
    }
}
