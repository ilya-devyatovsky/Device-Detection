/* *********************************************************************
 * This Source Code Form is copyright of 51Degrees Mobile Experts Limited. 
 * Copyright © 2014 51Degrees Mobile Experts Limited, 5 Charlotte Close,
 * Caversham, Reading, Berkshire, United Kingdom RG4 7BY
 * 
 * This Source Code Form is the subject of the following patent 
 * applications, owned by 51Degrees Mobile Experts Limited of 5 Charlotte
 * Close, Caversham, Reading, Berkshire, United Kingdom RG4 7BY: 
 * European Patent Application No. 13192291.6; and
 * United States Patent Application Nos. 14/085,223 and 14/085,301.
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0.
 * 
 * If a copy of the MPL was not distributed with this file, You can obtain
 * one at http://mozilla.org/MPL/2.0/.
 * 
 * This Source Code Form is “Incompatible With Secondary Licenses”, as
 * defined by the Mozilla Public License, v. 2.0.
 * ********************************************************************* */

using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;
using System.Collections.Specialized;
using System.Diagnostics;

namespace FiftyOne.Mobile.Detection.Provider.Interop
{
    /// <summary>
    /// Class used to wrap functions exposed by the tree matching C
    /// DLL.
    /// </summary>
    public class TrieWrapper : IWrapper
    {
        #region Classes

        /// <summary>
        /// The results of a device detection match. Must be disposed before the 
        /// provider used to create it is disposed.
        /// </summary>
        public class MatchResult : IMatchResult
        {
            /// <summary>
            /// Reference of the user agent used to create the results. Needed
            /// if the matched useragent characters are needed.
            /// </summary>
            private readonly string _userAgent;

            /// <summary>
            /// Reference of the provider used to create the results.
            /// </summary>
            private readonly TrieWrapper _provider;

            /// <summary>
            /// Memory used to retrieve the values.
            /// </summary>
            private readonly StringBuilder Values = new StringBuilder();

            /// <summary>
            /// Pointer to memory used to determine the offsets.
            /// </summary>
            private IntPtr _deviceOffsets;

            /// <summary>
            /// Constructs a new instance of the match results for the user
            /// agent provided.
            /// </summary>
            /// <param name="provider">Provider configured for device detection</param>
            /// <param name="userAgent">User agent to be detected</param>
            internal MatchResult(TrieWrapper provider, string userAgent)
            {
                _provider = provider;
                AllDeviceOffsetsReleased.Reset();
                Interlocked.Increment(ref AllocatedDeviceOffsets);
                _deviceOffsets = MatchFromUserAgent(userAgent);
                _userAgent = userAgent;
            }

            /// <summary>
            /// Constructs a new instance of the match result for the HTTP
            /// headers provided.
            /// </summary>
            /// <param name="provider">Provider configured for device detection</param>
            /// <param name="headers">HTTP headers of the request for detection</param>
            internal MatchResult(TrieWrapper provider, NameValueCollection headers)
            {
                _provider = provider;
                AllDeviceOffsetsReleased.Reset();
                Interlocked.Increment(ref AllocatedDeviceOffsets);
                var httpHeaders = new StringBuilder();
                for (int i = 0; i < headers.Count; i++)
                {
                    httpHeaders.AppendLine(String.Format("{0}: {1}",
                        headers.Keys[i],
                        String.Concat(headers.GetValues(i))));
                }
                _userAgent = headers["User-Agent"];
                _deviceOffsets = MatchFromHeaders(httpHeaders);
            }

            /// <summary>
            /// Ensures any unmanaged memory is freed if dispose didn't run
            /// for any reason.
            /// </summary>
            ~MatchResult()
            {
                Disposing(false);
            }

            /// <summary>
            /// Frees unmanged memory when the instance is disposed.
            /// </summary>
            public void Dispose()
            {
                Disposing(true);
                GC.SuppressFinalize(true);
            }

            /// <summary>
            /// Releases the pointer to the workset back to the pool.
            /// </summary>
            /// <param name="disposing"></param>
            protected virtual void Disposing(bool disposing)
            {
                if (_deviceOffsets != IntPtr.Zero)
                {
                    FreeMatchResult(_deviceOffsets);
                    _deviceOffsets = IntPtr.Zero;

                    // Reduce the number of device offsets that are allocated.
                    Interlocked.Decrement(ref AllocatedDeviceOffsets);
                    // If the allocated device offsets are now zero then signal
                    // the provider to release the memory used
                    // if it's being disposed of. Needed to ensure that
                    // all device detection is completed before disposing of 
                    // the provider.
                    if (AllocatedDeviceOffsets == 0)
                    {
                        AllDeviceOffsetsReleased.Set();
                    }
                }
            }

            /// <summary>
            /// Returns the values for the property provided.
            /// </summary>
            /// <param name="propertyName"></param>
            /// <returns>Value of the property, otherwise null.</returns>
            public string this[string propertyName]
            {
                get
                {
                    int index;
                    if (_provider.PropertyIndexes.TryGetValue(propertyName, out index))
                    {
                        // Get the number of characters written. If the result is negative
                        // then this indicates that the values string builder needs to be
                        // set to the positive value and the method recalled.
                        var charactersWritten = GetPropertyValues(_deviceOffsets, index, Values, Values.Capacity);
                        if (charactersWritten < 0)
                        {
                            Values.Capacity = Math.Abs(charactersWritten);
                            charactersWritten = GetPropertyValues(_deviceOffsets, index, Values, Values.Capacity);
                        }
                        return Values.ToString();
                    }
                    return null;
                }
            }

            /// <summary>
            /// A string representation of the user agent returned if any.
            /// </summary>
            public string UserAgent
            {
                get
                {
                    if (_userAgent != null)
                    {
                        return _userAgent.Substring(0, GetMatchedUserAgentLength(_userAgent));
                    }
                    return null;
                }
            }
        }

        #endregion

        #region DLL Imports

        [DllImport("FiftyOne.Mobile.Detection.Provider.Trie.dll",
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi,
            SetLastError = true)]
        private static extern int InitWithPropertyString(String fileName, String properties);

        [DllImport("FiftyOne.Mobile.Detection.Provider.Trie.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern void Destroy();

        [DllImport("FiftyOne.Mobile.Detection.Provider.Trie.dll",
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        private static extern int GetRequiredPropertyIndex(String propertyName);

        [DllImport("FiftyOne.Mobile.Detection.Provider.Trie.dll",
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        private static extern int GetRequiredPropertyName(int requiredPropertyIndex, StringBuilder propertyName, int size);

        [DllImport("FiftyOne.Mobile.Detection.Provider.Trie.dll",
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        private static extern int GetHttpHeaderName(int httpHeaderIndex, StringBuilder httpHeader, int size);

        [DllImport("FiftyOne.Mobile.Detection.Provider.Trie.dll",
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        private static extern IntPtr MatchFromUserAgent(String userAgent);

        [DllImport("FiftyOne.Mobile.Detection.Provider.Trie.dll",
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        private static extern int GetMatchedUserAgentLength(String userAgent);

        [DllImport("FiftyOne.Mobile.Detection.Provider.Trie.dll",
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        private static extern IntPtr MatchFromHeaders(StringBuilder httpHeaders);

        [DllImport("FiftyOne.Mobile.Detection.Provider.Trie.dll",
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        private static extern int GetPropertyValues(IntPtr deviceOffsets, int requiredPropertyIndex, StringBuilder values, int size);

        [DllImport("FiftyOne.Mobile.Detection.Provider.Trie.dll",
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        private static extern void FreeMatchResult(IntPtr deviceOffsets);

        #endregion

        #region Fields

        /// <summary>
        /// Used to synchronise the releasing of unmanaged memory resources.
        /// </summary>
        internal static readonly AutoResetEvent AllDeviceOffsetsReleased =
            new AutoResetEvent(true);

        /// <summary>
        /// The number of allocated device offsets. Used to ensure they've
        /// all been disposed of before the provider is disposed.
        /// </summary>
        internal static int AllocatedDeviceOffsets = 0;

        /// <summary>
        /// The number of instances of the wrapper.
        /// </summary>
        private static int _instanceCount = 0;

        /// <summary>
        /// Used to lock initialise and destroy calls.
        /// </summary>
        private static object _lock = new Object();

        /// <summary>
        /// Collection of property names to indexes.
        /// </summary>
        internal readonly SortedList<string, int> PropertyIndexes = new SortedList<string, int>();

        /// <summary>
        /// The name of the file used to create the current 
        /// single underlying provider.
        /// </summary>
        private static string _fileName;

        /// <summary>
        /// Set to true when dispose has run for the wrapper
        /// as sometimes the finaliser still runs even when
        /// requested not to.
        /// </summary>
        private bool _disposed = false;

        #endregion

        #region Constructor and Destructor

        /// <summary>
        /// Construct the wrapper.
        /// </summary>
        /// <param name="fileName">The full path to the file containing device data.</param>
        public TrieWrapper(string fileName) : this(fileName, String.Empty) { }

        /// <summary>
        /// Construct the wrapper.
        /// </summary>
        /// <param name="fileName">The full path to the file containing device data.</param>
        /// <param name="properties">Array of properties to include in the results.</param>
        public TrieWrapper(string fileName, string[] properties) : this(fileName, String.Join(",", properties)) { }

        /// <summary>
        /// Construct the wrapper.
        /// </summary>
        /// <param name="fileName">The full path to the file containing device data.</param>
        /// <param name="properties">Comma seperated list of properties to include in the results.</param>
        public TrieWrapper(string fileName, string properties)
        {
            lock (_lock)
            {
                // Check the file exists before trying to load it.
                var info = new FileInfo(fileName);
                if (info.Exists == false)
                {
                    throw new ArgumentException(String.Format(
                        "File '{0}' can not be found.",
                        info.FullName), "fileName");
                }

                // If a file has already been loaded then check it's the 
                // same name as the one being used for this instance. Only
                // one file can be loaded at a time.
                if (_fileName != null &&
                    _fileName.Equals(fileName) == false)
                {
                    throw new ArgumentException(String.Format(
                        "Trie has already been initialised with file name '{0}'. " +
                        "Multiple providers with different file sources can not be created.",
                        _fileName), "fileName");
                }

                // Only initialise the memory if the file has not already
                // been loaded into memory.
                if (_fileName == null)
                {
                    var status = InitWithPropertyString(info.FullName, properties);
                    if (status != 0)
                    {
                        throw new Exception(String.Format(
                            "Status code '{0}' returned when creating wrapper from file '{1}'.",
                            status,
                            fileName));
                    }

                    // Initialise the list of property names and indexes.
                    var propertyIndex = 0;
                    var property = new StringBuilder(256);
                    while (GetRequiredPropertyName(propertyIndex, property, property.Capacity) > 0)
                    {
                        PropertyIndexes.Add(property.ToString(), propertyIndex);
                        propertyIndex++;
                    }

                    // Initialise the list of http header names.
                    var httpHeaderIndex = 0;
                    var httpHeader = new StringBuilder(256);
                    while (GetHttpHeaderName(httpHeaderIndex, httpHeader, httpHeader.Capacity) > 0)
                    {
                        HttpHeaders.Add(httpHeader.ToString());
                        httpHeaderIndex++;
                    }
                    HttpHeaders.Sort();

                    _fileName = fileName;
                }

                // Increase the number of wrapper instances that have
                // been created. Used when the wrapper is disposed to 
                // determine if the memory used should be released.
                _instanceCount++;
            }
        }

        /// <summary>
        /// Ensure the memory used by trie has been freed.
        /// </summary>
        ~TrieWrapper()
        {
            Disposing(false);
        }

        /// <summary>
        /// When disposed of correctly ensures all memory is freed.
        /// </summary>
        public void Dispose()
        {
            Disposing(true);
            GC.SuppressFinalize(true);
        }

        /// <summary>
        /// If the instance count is zero disposes of the memory.
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Disposing(bool disposing)
        {
            lock (_lock)
            {
                if (_disposed == false)
                {
                    if (_instanceCount == 1 &&
                        _fileName != null)
                    {
                        // Wait for any detections to complete.
                        AllDeviceOffsetsReleased.WaitOne();

                        // Clear down any static data and free memory.
                        HttpHeaders.Clear();
                        PropertyIndexes.Clear();
                        _fileName = null;
                        Destroy();
                        _disposed = true;

                        // Set so that the next wrapper to be disposed of
                        // does not get stuck if no detections are performed.
                        AllDeviceOffsetsReleased.Set();

                        Debug.WriteLine("Freed Trie Data");
                    }
                    _instanceCount--;
                    _disposed = true;
                }
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// A list of the http headers that the wrapper can use for detection.
        /// </summary>
        public List<string> HttpHeaders
        {
            get { return _httpHeaders; }
        }
        private readonly List<string> _httpHeaders = new List<string>();

        /// <summary>
        /// A list of properties available from the provider.
        /// </summary>
        public IList<string> AvailableProperties
        {
            get { return PropertyIndexes.Keys; }
        }

        /// <summary>
        /// Returns a list of properties and values for the userAgent provided.
        /// </summary>
        /// <param name="userAgent">The useragent to search for.</param>
        /// <returns>A list of properties.</returns>
        public IMatchResult Match(string userAgent)
        {
            return new MatchResult(this, userAgent);
        }

        public IMatchResult Match(NameValueCollection headers)
        {
            return new MatchResult(this, headers);
        }

        #endregion
    }
}