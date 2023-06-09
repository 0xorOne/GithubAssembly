﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Midl2Bytes
{
    //https://github.com/microsoft/WindowsProtocolTestSuites/blob/main/ProtoSDK/MS-RPCE/Stub/RpceStubHelper.cs
    internal class Program
    {
        private static void Main(string[] args)
        {
            string[] content;
            if (args.Length < 1)
            {
                Console.WriteLine("Midl2Bytes.exe <path to midl output .c file>");
                Console.WriteLine("Midl2Bytes.exe .\\x64\\ms-rprn_c.c");
                return;
            }
            if (File.Exists(args[0]))
            {
                content = File.ReadAllLines(args[0]);
            }
            else
            {
                Console.WriteLine("Could not find file");
                Console.WriteLine("Midl2Bytes <path to midl output>");
                return;
            }
            bool c = false;
            string architecture = "64";
            string typeFormatString = "";
            string procFormatString = "";

            foreach (string line in content)
            {
                if (line.Contains("_MIDL_TypeFormatString ="))
                {
                    c = true;
                }
                if (c)
                {
                    typeFormatString += $"{line}\n";
                }
                if (line.Contains("    };"))
                {
                    c = false;
                }
                if (line.Contains(" x86 Stack size/offset "))
                {
                    architecture = "86";
                }
            }
            foreach (string line in content)
            {
                if (line.Contains("_MIDL_ProcFormatString ="))
                {
                    c = true;
                }
                if (c)
                {
                    procFormatString += $"{line}\n";
                }
                if (line.Contains("    };"))
                {
                    c = false;
                }
                if (line.Contains(" x86 Stack size/offset "))
                {
                    architecture = "86";
                }
            }

            try
            {
                //Console.WriteLine(typeFormatString);
                byte[] typeFormatStringBytes = RpceStubHelper.CreateFormatStringByteArray(typeFormatString);
                Console.WriteLine($"private static byte[] MIDL_TypeFormatStringx{architecture} = new byte[] {{" + PrintHexBytes(typeFormatStringBytes).TrimEnd(new char[] { ' ', ',' }) + "};");

                //Console.WriteLine(typeFormatString);
                byte[] ProcFormatStringBytes = RpceStubHelper.CreateFormatStringByteArray(procFormatString);
                Console.WriteLine($"private static byte[] MIDL_ProcFormatStringx{architecture} = new byte[] {{" + PrintHexBytes(ProcFormatStringBytes).TrimEnd(new char[] { ' ', ',' }) + "};");
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        public static string PrintHexBytes(byte[] byteArray)
        {
            var res = new StringBuilder(byteArray.Length * 3);
            for (var i = 0; i < byteArray.Length; i++)
                res.AppendFormat(NumberFormatInfo.InvariantInfo, "0x{0:x2}, ", byteArray[i]);
            return res.ToString();
        }
    }

    /// <summary>
    /// Helper class for encode/decode RPC stub.<para/>
    /// </summary>
    public static class RpceStubHelper
    {
        /// <summary>
        /// Get a byte array from the format string generated by midl.exe.<para/>
        /// Example: Regarding following code generated by midl.exe<para/>
        /// static const foo_MIDL_TYPE_FORMAT_STRING foo__MIDL_TypeFormatString =<para/>
        /// {<para/>
        ///     0,<para/>
        ///     {<para/>
        ///         NdrFcShort( 0x0 ),	/* 0 */<para/>
        /// /*  2 */<para/>
        ///         0x11, 0x0,	/* FC_RP */<para/>
        /// /*  4 */<para/>
        ///         NdrFcShort( 0x10 ),	/* Offset= 16 (20) */<para/>
        /// /*  6 */<para/>
        ///         0x1c,		/* FC_CVARRAY */<para/>
        ///         ...<para/>
        ///     }<para/>
        /// };<para/>
        /// The formatString parameter should be a substring
        /// start from "NdrFcShort(0x0)" and end at first"}".
        /// </summary>
        /// <param name="formatString">
        /// A format string generated by midl.exe. See method help for details.
        /// </param>
        /// <returns>A byte array of the format string passed to the method.</returns>
        public static byte[] CreateFormatStringByteArray(string formatString)
        {
            int index = 0;
            List<byte> formatStringBuffer = new List<byte>();
            Regex regex = new Regex(@"
0x (?<byte>[0-9a-fA-F]{1,2}) \s* ,
|
NdrFcShort \( \s* 0x (?<short>[0-9a-fA-F]{1,4}) \s* \)
|
NdrFcLong \( \s* 0x (?<long>[0-9a-fA-F]+) \s* \)
|
\n \/\* \s* (?<offset>\d+)* \s* \*\/
|
\/\* .* \*\/
",
                RegexOptions.IgnorePatternWhitespace);

            while (true)
            {
                Match match = regex.Match(formatString, index);
                if (!match.Success)
                {
                    break;
                }
                else if (match.Groups["byte"].Success)
                {
                    byte value = Convert.ToByte(match.Groups["byte"].Value, 16);
                    formatStringBuffer.Add(value);
                }
                else if (match.Groups["short"].Success)
                {
                    ushort value = Convert.ToUInt16(match.Groups["short"].Value, 16);
                    formatStringBuffer.Add((byte)(value & 0xff));
                    formatStringBuffer.Add((byte)(value >> 8));
                }
                else if (match.Groups["long"].Success)
                {
                    uint value = Convert.ToUInt32(match.Groups["long"].Value, 16);
                    formatStringBuffer.Add((byte)(value & 0xff));
                    formatStringBuffer.Add((byte)((value >> 8) & 0xff));
                    formatStringBuffer.Add((byte)((value >> 16) & 0xff));
                    formatStringBuffer.Add((byte)(value >> 24));
                }
                else if (match.Groups["offset"].Success)
                {
                    int offset = Convert.ToInt32(match.Groups["offset"].Value);
                    if (formatStringBuffer.Count != offset)
                        throw new InvalidOperationException();
                }
                index = match.Index + match.Length;
            }

            return formatStringBuffer.ToArray();
        }

        /// <summary>
        /// Parse procedure header a from byte array to structure.
        /// </summary>
        /// <param name="procFormatString">Proc format string.</param>
        /// <param name="offset">Offset of a procedure in procFormatString.</param>
        /// <returns>The RpceProcedureHeaderDescriptor.</returns>
        public static RpceProcedureHeaderDescriptor ParseProcedureHeaderDescriptor(
            byte[] procFormatString,
            ref int offset)
        {
            //handle_type<1>
            //Oi_flags<1>
            //[rpc_flags<4>]
            //proc_num<2>
            //stack_size<2>
            //[explicit_handle_description<>]
            //constant_client_buffer_size<2>
            //constant_server_buffer_size<2>
            //INTERPRETER_OPT_FLAGS<1>
            //number_of_params<1>
            //extension_size<1>
            //[extension]

            const byte FC_BIND_PRIMITIVE = 0x32;
            const int PRIMITIVE_HANDLE_LENGTH = 4;
            const int OTHER_HANDLE_LENGTH = 6;
            const byte OI_HAS_RPCFLAGS = 0x08;
            const int EXTENSION_SIZE = 8;

            RpceProcedureHeaderDescriptor procHeaderDesc = new RpceProcedureHeaderDescriptor();

            procHeaderDesc.HandleType = procFormatString[offset];
            offset += Marshal.SizeOf(procHeaderDesc.HandleType);
            procHeaderDesc.OiFlags = procFormatString[offset];
            offset += Marshal.SizeOf(procHeaderDesc.OiFlags);
            if ((procHeaderDesc.OiFlags & OI_HAS_RPCFLAGS) != 0)
            {
                procHeaderDesc.RpcFlags = BitConverter.ToUInt32(procFormatString, offset);
                offset += Marshal.SizeOf(procHeaderDesc.RpcFlags.Value);
            }
            procHeaderDesc.ProcNum = BitConverter.ToUInt16(procFormatString, offset);
            offset += Marshal.SizeOf(procHeaderDesc.ProcNum);
            procHeaderDesc.StackSize = BitConverter.ToUInt16(procFormatString, offset);
            offset += Marshal.SizeOf(procHeaderDesc.StackSize);
            if (procHeaderDesc.HandleType == 0) // explicit handle
            {
                int length = (procFormatString[offset] == FC_BIND_PRIMITIVE)
                    ? PRIMITIVE_HANDLE_LENGTH
                    : OTHER_HANDLE_LENGTH;
                procHeaderDesc.ExplicitHandleDescription = ArrayUtility.SubArray(procFormatString, offset, length);
                offset += length;
            }
            procHeaderDesc.ClientBufferSize = BitConverter.ToUInt16(procFormatString, offset);
            offset += Marshal.SizeOf(procHeaderDesc.ClientBufferSize);
            procHeaderDesc.ServerBufferSize = BitConverter.ToUInt16(procFormatString, offset);
            offset += Marshal.SizeOf(procHeaderDesc.ServerBufferSize);
            procHeaderDesc.InterpreterOptFlags = procFormatString[offset];
            offset += Marshal.SizeOf(procHeaderDesc.InterpreterOptFlags);
            procHeaderDesc.NumberOfParams = procFormatString[offset];
            offset += Marshal.SizeOf(procHeaderDesc.NumberOfParams);
            byte extensionSize = procFormatString[offset];
            if (extensionSize >= EXTENSION_SIZE)
            {
                procHeaderDesc.ExtensionSize = extensionSize;
                offset += Marshal.SizeOf(procHeaderDesc.ExtensionSize.Value);
                procHeaderDesc.ExtensionFlags2 = (RpceInterpreterOptFlags2)procFormatString[offset];
                offset += Marshal.SizeOf((byte)procHeaderDesc.ExtensionFlags2);
                procHeaderDesc.ExtensionClientCorrHint = BitConverter.ToUInt16(procFormatString, offset);
                offset += Marshal.SizeOf(procHeaderDesc.ExtensionClientCorrHint.Value);
                procHeaderDesc.ExtensionServerCorrHint = BitConverter.ToUInt16(procFormatString, offset);
                offset += Marshal.SizeOf(procHeaderDesc.ExtensionServerCorrHint.Value);
                procHeaderDesc.ExtensionNotifyIndex = BitConverter.ToUInt16(procFormatString, offset);
                offset += Marshal.SizeOf(procHeaderDesc.ExtensionNotifyIndex.Value);
                offset += extensionSize - EXTENSION_SIZE;
            }
            else
            {
                offset += extensionSize;
            }

            return procHeaderDesc;
        }

        public static class ArrayUtility
        {
            /// <summary>
            /// Compares two arrays
            /// </summary>
            /// <typeparam name="T">
            /// Type of array, must implement IComparable&lt;T&gt; interface
            /// </typeparam>
            /// <param name="array1">The first array</param>
            /// <param name="array2">The second array</param>
            /// <returns>True if the two arrays are equal, false otherwise</returns>
            public static bool CompareArrays<T>(T[] array1, T[] array2) where T : IComparable<T>
            {
                // Reference equal
                if (array1 == array2)
                {
                    return true;
                }
                // one of the arrays is null
                if (array1 == null || array2 == null)
                {
                    return false;
                }
                // The two arrays have different size
                if (array1.Length != array2.Length)
                {
                    return false;
                }
                for (int i = 0; i < array1.Length; i++)
                {
                    if (array1[i].CompareTo(array2[i]) != 0)
                    {
                        return false;
                    }
                }
                return true;
            }

            public static T[] SubArray<T>(T[] array, int startIndex, int length)
            {
                T[] subArray = new T[length];
                Array.Copy(array, startIndex, subArray, 0, length);

                return subArray;
            }
        }
    }
}