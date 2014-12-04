using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Threading;
using System.Net;
using System.Net.Cache;
using System.ComponentModel;
using System.Data;
using System.Data.SQLite;
using System.Drawing;
using System.Diagnostics;
using System.Web;
using System.Collections;
using System.Threading.Tasks;

internal class HTTPWrapper
{
    #region Declarations

    //Declarations
    private const string conUserAgent = "Mozilla/5.0 (Windows NT 6.3; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/37.0.2062.120 Safari/537.36";
    private bool _UseGzip = true;
    private int _Timeout = 7000;
    private string _UserAgent = conUserAgent;
    private string _LastPage = "http://www.google.com/";
    private bool _UseProxy = false;
    private WebProxy _Proxy;
    private bool _UsePipelining = false;
    private bool _PrivateBrowsing = false;
    private ThreadPriority _ThreadPriority = ThreadPriority.AboveNormal;
    private Encoding _Encoding = Encoding.UTF8;
    private int _BufferSize = 8192;
    private int _SleepDelay = 75;
    private bool _UseCaching = true;


    //Cookies Declarations
    private CookieContainer _Cookies = new CookieContainer();
    private Cookie _Cookie;

    // Firefox Cookies
    private bool blnLockFirefox = false;
    private FileStream strmLockFirefox = null;

    // Chrome Cookies
    private bool blnLockChrome = false;
    private FileStream strmLockChrome = null;

    private ExceptionCatcherSub ExceptionCatcher = null;

    public delegate void ExceptionCatcherSub(object sender, Exception Ex);
    public delegate void DownloadCallback(DownloadManager dlDstate);
    public delegate void UploadCallback(UploadManager ulUstate);

    public HTTPWrapper()
    {
    }

    public HTTPWrapper(ExceptionCatcherSub ExCatcher)
    {
        ExceptionCatcher = ExCatcher;
    }

    #endregion


    #region Firefox Import/Export

    /// <summary>
    /// Locks the Firefox cookies based on file path given.  Returns whether locking was successful.
    /// </summary>
    /// <param name="strPath">Required. The string representation of the path to the Firefox cookies to lock.</param>
    /// <returns>Returns whether locking was successful.</returns>
    public bool LockFirefoxCookies(string strPath)
    {
        if (!File.Exists(strPath)) return false;
        else if (blnLockFirefox) UnlockFirefoxCookies();

        try
        {
            strmLockFirefox = File.Open(strPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch
        {
            if (strmLockFirefox != null)
                strmLockFirefox.Close();
            return false;
        }

        blnLockFirefox = true;
        return true;
    }


    /// <summary>
    /// Unlocks the Firefox cookies if the LockFirefoxCookies() method was previously called.
    /// </summary>
    public void UnlockFirefoxCookies()
    {
        if (strmLockFirefox != null)
            strmLockFirefox.Close();
        strmLockFirefox = null;
        blnLockFirefox = false;
        GC.Collect();
    }


    /// <summary>
    /// Imports Firefox cookies based on file path given.  Wrapper retains file lock even during import.
    /// </summary>
    /// <param name="strPath">Required. The string representation of the path to the cookies.</param>
    /// <param name="domains">Optional. The specified domains' cookies to import.  Null value indicates that all domains will be imported.</param>
    /// <param name="blnClearCookies">Optional. The boolean indicating whether to clear the wrapper's cookies before importing.</param>
    /// <returns></returns>
    public bool ImportFirefoxCookies(string strPath, string[] domains = null, bool blnClearCookies = true)
    {
        bool blnWasLocked = blnLockFirefox;
        if (!blnLockFirefox)
        {
            if (!LockFirefoxCookies(strPath))
                return false;
        }
        UnlockFirefoxCookies();

        if (blnClearCookies)
            ClearCookies();

        DataTable data = new DataTable();
        string query = "SELECT * FROM moz_cookies";

        if (domains != null)
        {
            query += string.Format(" WHERE baseDomain LIKE '{0}'", domains[0].Replace("http://", string.Empty).Replace("www.", string.Empty));
            if (domains.Length > 1)
            {
                for (int i = 1; i < domains.Length; i++)
                {
                    query += string.Format(" OR baseDomain LIKE '{0}'", domains[i].Replace("http://", string.Empty).Replace("www.", string.Empty));
                }
            }
        }

        SQLiteConnection connection = new SQLiteConnection("Data Source=" + strPath + ";pooling=false");
        try
        {
            connection.Open();
            SQLiteCommand command = connection.CreateCommand();

            using (SQLiteDataAdapter adapter = new SQLiteDataAdapter(query, connection))
            {
                adapter.Fill(data);
            }
            connection.Close();
            
            // Must clean up
            GC.Collect();
        }
        catch
        {
            connection.Close();

            // Must clean up
            GC.Collect();
            if (blnWasLocked)
                LockFirefoxCookies(strPath);
            return false;
        }

        {
            Cookie cookAdd;
            string strTmp;
            DateTime UNIX = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            foreach (DataRow row in data.Rows)
            {

                string strAddPath = row["path"].ToString();
                string strDomain = StandardizeURL(row["baseDomain"].ToString());

                cookAdd = new Cookie(row["name"].ToString(), row["value"].ToString(), strAddPath, strDomain);

                strTmp = row["expiry"].ToString();
                cookAdd.Expires = UNIX.AddSeconds(double.Parse(strTmp));
                cookAdd.HttpOnly = (row["isHttpOnly"].ToString() == "1");
                cookAdd.Secure = (row["isSecure"].ToString() == "1");

                AddCookie("http://" + strDomain + strAddPath, cookAdd);
            }
        }

        data.Dispose();

        if (blnWasLocked)
            LockFirefoxCookies(strPath);
        return true;
    }


    /// <summary>
    /// Exports the wrapper's cookies to Firefox based on file path given.  Wrapper retains file lock even during export.
    /// </summary>
    /// <param name="strPath">Required. The string representation of the path to the cookies.</param>
    /// <param name="domains">Optional. The specified domains' cookies to export. Null value indicates that all domains will be exported.</param>
    /// <param name="blnClearCookies">Optional. The boolean indicating whether to clear Firefox cookies before exporting.</param>
    /// <returns></returns>
    public int ExportFirefoxCookies(string strPath, string[] domains = null, bool blnClearCookies = false)
    {
        bool blnWasLocked = blnLockFirefox;
        if (!blnLockFirefox && !LockFirefoxCookies(strPath))
            return -1;

        UnlockFirefoxCookies();

        int intReturn = ExportFFInternal(strPath, domains, blnClearCookies);

        if (blnWasLocked)
            LockFirefoxCookies(strPath);

        return intReturn;
    }


    private int ExportFFInternal(string strPath, string[] domains = null, bool blnClearCookies = false)
    {
        // Cookie return count
        int intRet = -1;


        SQLiteConnection connection = new SQLiteConnection("Data Source=" + strPath + ";pooling=false");
        SQLiteDataAdapter adapter;


        DataSet dataSet = new DataSet();
        DataTable data;

        try
        {
            connection.Open();
            adapter = new SQLiteDataAdapter("Select * FROM moz_cookies", connection);
            adapter.Fill(dataSet, "moz_cookies");
            data = dataSet.Tables[0];

            connection.Close();
        }
        catch
        {
            connection.Close();

            // Must clean up
            GC.Collect();
            return intRet;
        }

        {
            CookieCollection cookies;
            bool blnExists;
            DataRow drEdit = null;
            DateTime UNIX = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            if (blnClearCookies)
            {
                data.Rows.Clear();
            }

            if (domains == null || domains.Length < 1)
            {
                cookies = GetAllCookies();
                foreach (Cookie cookie in cookies)
                {
                    blnExists = false;
                    foreach (DataRow row in data.Rows)
                    {
                        if (StandardizeURL(row["host"].ToString()).Equals(StandardizeURL(cookie.Domain)) && row["name"].ToString().Equals(cookie.Name))
                        {
                            drEdit = row;
                            blnExists = true;
                            break;
                        }
                    }

                    if (!blnExists)
                    {
                        drEdit = data.NewRow();
                        data.Rows.Add(drEdit);
                        Uri uri = new Uri(StandardizeURL(cookie.Domain, true));

                        drEdit["baseDomain"] = uri.Host;
                        drEdit["host"] = uri.Host;
                        drEdit["name"] = cookie.Name;
                        drEdit["creationTime"] = ((long)(cookie.TimeStamp.ToUniversalTime() - UNIX).TotalMilliseconds).ToString() + "000";
                        drEdit["appId"] = 0;
                        drEdit["inBrowserElement"] = 0;
                    }

                    drEdit["value"] = cookie.Value;
                    drEdit["expiry"] = (long)(cookie.Expires.ToUniversalTime() - UNIX).TotalSeconds;
                    drEdit["lastAccessed"] = ((long)(DateTime.UtcNow - UNIX).TotalMilliseconds).ToString() + "000";
                    drEdit["path"] = cookie.Path;
                    drEdit["isSecure"] = Convert.ToInt32(cookie.Secure);
                    drEdit["isHttpOnly"] = Convert.ToInt32(cookie.HttpOnly);
                }

            }
            else
            {
                foreach (string url in domains)
                {
                    cookies = GetCookieCollection(url);
                    foreach (Cookie cookie in cookies)
                    {
                        blnExists = false;
                        foreach (DataRow row in data.Rows)
                        {
                            if (StandardizeURL(row["host"].ToString()).Equals(StandardizeURL(cookie.Domain)) && row["name"].ToString().Equals(cookie.Name))
                            {
                                drEdit = row;

                                blnExists = true;
                                break;
                            }
                        }

                        if (!blnExists)
                        {
                            drEdit = data.NewRow();
                            data.Rows.Add(drEdit);
                            Uri uri = new Uri(StandardizeURL(cookie.Domain));

                            drEdit["baseDomain"] = uri.Host;
                            drEdit["host"] = uri.Host;
                            drEdit["name"] = cookie.Name;
                            drEdit["creationTime"] = ((long)(cookie.TimeStamp.ToUniversalTime() - UNIX).TotalMilliseconds).ToString() + "000";
                            drEdit["appId"] = 0;
                            drEdit["inBrowserElement"] = 0;
                        }

                        drEdit["value"] = cookie.Value;
                        drEdit["expiry"] = (cookie.Expires - UNIX).TotalSeconds.ToString();
                        drEdit["lastAccessed"] = (DateTime.UtcNow - UNIX).TotalSeconds.ToString() + "000";
                        drEdit["path"] = cookie.Path;
                        drEdit["isSecure"] = Convert.ToInt32(cookie.Secure);
                        drEdit["isHttpOnly"] = Convert.ToInt32(cookie.HttpOnly);
                    }
                }
            }
        }


        try
        {
            SQLiteCommandBuilder builder = new SQLiteCommandBuilder(adapter);
            connection.Open();
            if (blnClearCookies)
            {
                SQLiteCommand command = connection.CreateCommand();
                command.CommandText = "DELETE FROM moz_cookies";
                command.ExecuteNonQuery();
                command.Dispose();
            }

            intRet = adapter.Update(dataSet, "moz_cookies");

            adapter.Dispose();
            connection.Close();
        }
        catch
        {
            connection.Close();
        }

        // Must clean up
        GC.Collect();
        return intRet;
    }


    /// <summary>
    /// Attempts to detect the location of the cookies of Firefox.
    /// </summary>
    /// <param name="strPath">Required. The referenced string that will receive the path value.</param>
    /// <returns>Returns whether detection was successful.</returns>
    public bool DetectFirefoxCookieLocation(out string strPath)
    {
        // initialize default value for strPath
        strPath = string.Empty;

        // Find AppData
        string strTemp = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\Mozilla\\Firefox\\Profiles";

        if (!Directory.Exists(strTemp))
            return false;

        // Search for .default profile
        try
        {
            DirectoryInfo di = new DirectoryInfo(strTemp);
            var search = di.EnumerateDirectories("*.default");

            if (search.Count() != 1)
                return false;
            else
                strTemp = search.ElementAt(0).FullName + "\\cookies.sqlite";
        }
        catch
        {
            return false;
        }
        
        // Check for SQLite database
        if (!File.Exists(strTemp))
            return false;

        strPath = strTemp;
        return true;

    }

    #endregion


    #region Chrome Import/Export

    /// <summary>
    /// Locks the Chrome cookies based on file path given.  Returns whether locking was successful.
    /// </summary>
    /// <param name="strPath">Required. The string representation of the path to the Firefox cookies to lock.</param>
    /// <returns>Returns whether locking was successful.</returns>
    public bool LockChromeCookies(string strPath)
    {
        if (!File.Exists(strPath))
            return false;
        else if (blnLockChrome)
            UnlockChromeCookies();

        try
        {
            strmLockChrome = File.Open(strPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch
        {
            if (strmLockChrome != null)
                strmLockChrome.Close();
            return false;
        }

        blnLockChrome = true;
        return true;
    }


    /// <summary>
    /// Unlocks the Firefox cookies if the LockChromeCookies() method was previously called.
    /// </summary>
    public void UnlockChromeCookies()
    {
        if (strmLockChrome != null)
            strmLockChrome.Close();
        strmLockChrome = null;
        blnLockChrome = false;
    }


    /// <summary>
    /// Imports Chrome cookies based on file path given.  Wrapper retains file lock even during import.
    /// </summary>
    /// <param name="strPath">Required. The string representation of the path to the cookies.</param>
    /// <param name="domains">Optional. The specified domains' cookies to import.  Null value indicates that all domains will be imported.</param>
    /// <param name="blnClearCookies">Optional. The boolean indicating whether to clear the wrapper's cookies before importing.</param>
    /// <returns></returns>
    public bool ImportChromeCookies(string strPath, string[] domains = null, bool blnClearCookies = true)
    {
        bool blnWasLocked = blnLockChrome;
        if (!blnLockChrome)
        {
            if (!LockChromeCookies(strPath)) return false;
        }
        UnlockChromeCookies();

        if (blnClearCookies) ClearCookies();

        DataTable data = new DataTable();
        string query = "SELECT * FROM cookies";
        SQLiteConnection connection = new SQLiteConnection("Data Source=" + strPath + ";pooling=false");

        try
        {
            connection.Open();
            SQLiteCommand command = connection.CreateCommand();

            using (SQLiteDataAdapter adapter = new SQLiteDataAdapter(query, connection))
            {
                adapter.Fill(data);
            }
            connection.Close();
            // Must clean up
            GC.Collect();
        }
        catch
        {
            connection.Close();
            // Must clean up
            GC.Collect();
            if (blnWasLocked) LockChromeCookies(strPath);
            return false;
        }

        {
            Cookie cookAdd;
            string strTmp;
            DateTime WEBKIT = new DateTime(1961, 1, 1, 0, 0, 0);

            foreach (DataRow row in data.Rows)
            {
                string strDomain = StandardizeURL(row["host_key"].ToString());

                // Check if domain is under one of the domains
                if (domains != null)
                {
                    bool blnFound = false;
                    foreach (string s in domains)
                    {
                        if (s.Contains(strDomain))
                        {
                            blnFound = true;
                            break;
                        }
                    }
                    if (!blnFound) continue;
                }

                string strAddPath = row["path"].ToString();
                cookAdd = new Cookie(row["name"].ToString(), row["value"].ToString(), strAddPath, strDomain);

                strTmp = row["expires_utc"].ToString();
                cookAdd.Expires = WEBKIT.AddMilliseconds(Math.Round(double.Parse(strTmp) / 1000D, 0, MidpointRounding.AwayFromZero));
                cookAdd.HttpOnly = (row["httponly"].ToString() == "1");
                cookAdd.Secure = (row["secure"].ToString() == "1");

                AddCookie("http://" + strDomain + strAddPath, cookAdd);
            }
        }


        if (blnWasLocked)
            LockChromeCookies(strPath);
        return true;
    }


    /// <summary>
    /// Exports the wrapper's cookies to Chrome based on file path given.  Wrapper retains file lock even during export.
    /// </summary>
    /// <param name="strPath">Required. The string representation of the path to the cookies.</param>
    /// <param name="domains">Optional. The specified domains' cookies to export. Null value indicates that all domains will be exported.</param>
    /// <param name="blnClearCookies">Optional. The boolean indicating whether to clear Firefox cookies before exporting.</param>
    /// <returns></returns>
    public int ExportChromeCookies(string strPath, string[] domains = null, bool blnClearCookies = false)
    {
        bool blnWasLocked = blnLockChrome;
        if (!blnLockChrome && !LockChromeCookies(strPath))
            return -1;
        UnlockChromeCookies();

        int intReturn = ExportCHInternal(strPath, domains, blnClearCookies);

        if (blnWasLocked)
            LockChromeCookies(strPath);
        return intReturn;
    }


    private int ExportCHInternal(string strPath, string[] domains = null, bool blnClearCookies = false)
    {
        int intRet = -1;


        SQLiteConnection connection = new SQLiteConnection("Data Source=" + strPath + ";pooling=false");
        SQLiteDataAdapter adapter;


        DataSet dataSet = new DataSet();
        DataTable data;

        try
        {
            connection.Open();
            adapter = new SQLiteDataAdapter("Select * FROM cookies", connection);
            adapter.Fill(dataSet, "cookies");
            data = dataSet.Tables[0];

            connection.Close();
        }
        catch
        {
            connection.Close();

            // Must clean up
            GC.Collect();
            return intRet;
        }

        {
            CookieCollection cookies;
            bool blnExists;
            DataRow drEdit = null;
            DateTime WEBKIT = new DateTime(1961, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            if (blnClearCookies) data.Clear();

            if (domains == null || domains.Length < 1)
            {
                cookies = GetAllCookies();
                foreach (Cookie cookie in cookies)
                {
                    blnExists = false;
                    foreach (DataRow row in data.Rows)
                    {
                        if (StandardizeURL(row["host"].ToString()).Equals(StandardizeURL(cookie.Domain)) && row["name"].ToString().Equals(cookie.Name))
                        {
                            drEdit = row;

                            blnExists = true;
                            break;
                        }
                    }

                    if (!blnExists)
                    {
                        drEdit = data.NewRow();
                        data.Rows.Add(drEdit);

                        Uri uri = new Uri(StandardizeURL(cookie.Domain, true));

                        drEdit["host_key"] = "." + uri.Host;
                        drEdit["creation_utc"] = ((long)(cookie.TimeStamp - WEBKIT).TotalSeconds).ToString() + RandomString("1234567890", 6);
                        drEdit["has_expires"] = 1;
                        drEdit["persistent"] = 1;
                    }

                    drEdit["value"] = cookie.Value;
                    drEdit["expires_utc"] = ((long)(cookie.Expires - WEBKIT).TotalSeconds).ToString() + RandomString("1234567890", 6);
                    drEdit["last_access_utc"] = ((long)(DateTime.UtcNow - WEBKIT).TotalSeconds).ToString() + RandomString("1234567890", 6);
                    drEdit["path"] = cookie.Path;
                    drEdit["secure"] = Convert.ToInt32(cookie.Secure);
                    drEdit["httponly"] = Convert.ToInt32(cookie.HttpOnly);
                }
            }
            else
            {
                foreach (string url in domains)
                {
                    cookies = GetCookieCollection(url);
                    foreach (Cookie cookie in cookies)
                    {
                        blnExists = false;
                        foreach (DataRow row in data.Rows)
                        {
                            if (StandardizeURL(row["host"].ToString()).Equals(StandardizeURL(cookie.Domain)) && row["name"].ToString().Equals(cookie.Name))
                            {
                                drEdit = row;

                                blnExists = true;
                                break;
                            }
                        }

                        if (!blnExists)
                        {
                            drEdit = data.NewRow();
                            data.Rows.Add(drEdit);

                            Uri uri = new Uri(StandardizeURL(cookie.Domain, true));

                            drEdit["host_key"] = "." + uri.Host;
                            drEdit["creation_utc"] = ((long)(cookie.TimeStamp - WEBKIT).TotalSeconds).ToString() + RandomString("1234567890", 6);
                            drEdit["has_expires"] = 1;
                            drEdit["persistent"] = 1;
                        }

                        drEdit["value"] = cookie.Value;
                        drEdit["expires_utc"] = ((long)(cookie.Expires - WEBKIT).TotalSeconds).ToString() + RandomString("1234567890", 6);
                        drEdit["last_access_utc"] = ((long)(DateTime.UtcNow - WEBKIT).TotalSeconds).ToString() + RandomString("1234567890", 6);
                        drEdit["path"] = cookie.Path;
                        drEdit["secure"] = Convert.ToInt32(cookie.Secure);
                        drEdit["httponly"] = Convert.ToInt32(cookie.HttpOnly);
                    }
                }
            }
        }

        try
        {
            SQLiteCommandBuilder builder = new SQLiteCommandBuilder(adapter);
            connection.Open();
            if (blnClearCookies)
            {
                SQLiteCommand command = connection.CreateCommand();
                command.CommandText = "DELETE FROM cookies";
                command.ExecuteNonQuery();
                command.Dispose();
            }
            intRet = adapter.Update(dataSet, "cookies");

            adapter.Dispose();
            connection.Close();
        }
        catch
        {
            connection.Close();
        }

        GC.Collect();
        return intRet;
    }


    /// <summary>
    /// Attempts to detect the location of the cookies of Chrome.
    /// </summary>
    /// <param name="strPath">Required. The referenced string that will receive the path value.</param>
    /// <returns>Returns whether detection was successful.</returns>
    public bool DetectChromeCookieLocation(out string strPath)
    {
        strPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Google\\Chrome\\User Data\\Default\\cookies";

        if (!File.Exists(strPath))
        {
            strPath = string.Empty;
            return false;
        }
            
        return true;
    }

    #endregion


    #region Properties

    /// <summary>
    /// Sets the exception catcher.
    /// </summary>
    public ExceptionCatcherSub ExCatcher
    {
        set { ExceptionCatcher = value; }
    }

    /// <summary>
    /// Returns the constant default user agent of the wrapper. This property is read-only.
    /// </summary>
    public string DefaultUserAgent
    {
        get { return conUserAgent; }
    }

    /// <summary>
    /// Gets or sets a value that indicates whether the wrapper is currently using a proxy.
    /// </summary>
    public bool UseProxy
    {
        get { return _UseProxy; }
        set { _UseProxy = value; }
    }

    /// <summary>
    /// Gets or sets the proxy to use if UseProxy is true.
    /// </summary>
    public WebProxy Proxy
    {
        get { return _Proxy; }
        set { _Proxy = value; }
    }

    /// <summary>
    /// Returns a value that represents the last visited page of the wrapper. This property is read-only.
    /// </summary>
    public string LastPage
    {
        get { return _LastPage; }
    }

    /// <summary>
    /// Gets or sets a value that represents the user-agent being imitated by the wrapper.
    /// </summary>
    public string UserAgent
    {
        get { return _UserAgent; }
        set { _UserAgent = value; }
    }

    /// <summary>
    /// Gets or sets a value in milliseconds that indicates the time after initiating a request to wait before timing out.
    /// </summary>
    public int Timeout
    {
        get { return _Timeout; }
        set { _Timeout = Math.Abs(value); }
    }

    /// <summary>
    /// Gets or sets a value that indicates whether the wrapper is currently using Gzip Compression.
    /// </summary>
    public bool UseGZip
    {
        get { return _UseGzip; }
        set { _UseGzip = value; }
    }

    /// <summary>
    /// Gets or sets a value that indicates whether to pipeline to each request.
    /// </summary>
    public bool UsePipelining
    {
        get { return _UsePipelining; }
        set { _UsePipelining = value; }
    }

    /// <summary>
    /// Gets or sets a value that indicates whether each request will be recorded as the last page.
    /// </summary>
    public bool PrivateBrowsing
    {
        get { return _PrivateBrowsing; }
        set { _PrivateBrowsing = value; }
    }

    /// <summary>
    /// Gets or sets the cookies for the wrapper to use.
    /// </summary>
    public CookieContainer Cookies
    {
        get { return _Cookies; }
        set { _Cookies = value; }
    }

    /// <summary>
    /// Gets or sets the thread priority of the asynchronous thread for requests.
    /// </summary>
    public ThreadPriority ThreadPriority
    {
        get { return _ThreadPriority; }
        set { _ThreadPriority = value; }
    }

    /// <summary>
    /// Gets or sets the encoding to use.
    /// </summary>
    public Encoding Encoding
    {
        get { return _Encoding; }
        set { _Encoding = value; }
    }

    /// <summary>
    /// Gets or sets the buffer size to use when reading streams.
    /// </summary>
    public int BufferSize
    {
        get { return _BufferSize; }
        set { _BufferSize = value; }
    }

    /// <summary>
    /// Gets or sets the delay in milliseconds between checking for page load completion.
    /// </summary>
    public int SleepDelay
    {
        get { return _SleepDelay; }
        set { _SleepDelay = value; }
    }

    /// <summary>
    /// Gets or sets whether wrapper implements caching.
    /// </summary>
    public bool UseCaching
    {
        get { return _UseCaching; }
        set { _UseCaching = value; }
    }

    #endregion


    #region Functioning

    /// <summary>
    /// Sets the current proxy used by the wrapper and automatically sets the UseProxy property to True.
    /// </summary>
    /// <param name="strProxyHost">Required. The string representation of a proxy host or server.</param>
    /// <param name="intProxyPort">Required. The corresponding port of the proxy host or server.</param>
    public void SetProxy(string strProxyHost, int intProxyPort)
    {
        _Proxy = new WebProxy(strProxyHost, intProxyPort);
        _UseProxy = true;
    }


    /// <summary>
    /// Sets the current proxy used by the wrapper and automatically sets the UseProxy property to True.  A return value of whether the parsing was successful.
    /// </summary>
    /// <param name="strProxyHost">Required. The string representation of a proxy host or server.</param>
    /// <param name="strProxyPort">Required. The string representation of the corresponding port of the proxy host or server.</param>
    /// <returns>A return value of whether the parsing was successful.</returns>
    public bool SetProxy(string strProxyHost, string strProxyPort)
    {
        try
        {
            int intPort = int.Parse(strProxyPort);
            _Proxy = new WebProxy(strProxyHost, intPort);
            _UseProxy = true;
            return true;
        }
        catch (Exception ex)
        {
            Debug.Print(ex.Message);
            return false;
        }
    }


    /// <summary>
    /// Parses a proxy in 'Proxy:Port' string format and automatically sets the UseProxy property to True.  A return value of whether the parsing was successful.
    /// </summary>
    /// <param name="strProxy">Required. The string representation of a proxy in 'Proxy:Port' format.</param>
    /// <returns>A return value of whether the parsing was successful.</returns>
    public bool SetProxy(string strProxy)
    {
        string strProxyHost = string.Empty;
        int iProxyPort;
        string strPort = string.Empty;

        try
        {
            // Proxy Host
            strProxyHost = strProxy.Substring(0, strProxy.IndexOf(":"));

            // Port
            strPort = strProxy.Substring(strProxy.IndexOf(":") + 1);
            iProxyPort = int.Parse(strPort);

            //Transfer data
            _Proxy = new WebProxy(strProxyHost, iProxyPort);
            _UseProxy = true;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }


    /// <summary>
    /// Sets the credentials to use with the wrapper's proxy.
    /// </summary>
    /// <param name="credentials">Required. The credentials to set with the proxy.</param>
    public void SetProxyCredentials(NetworkCredential credentials)
    {
        if (_Proxy != null) _Proxy.Credentials = credentials;
    }


    /// <summary>
    /// Sets the credentials to use with the wrapper's proxy.
    /// </summary>
    /// <param name="strUsername">Required. The username of the credentials to set.</param>
    /// <param name="strPassword">Required. The password of the credentials to set.</param>
    public void SetProxyCredentials(string strUsername, string strPassword)
    {
        SetProxyCredentials(new NetworkCredential(strUsername, strPassword));
    }


    /// <summary>
    /// Deletes all cookies and logins.
    /// </summary>
    public void ClearCookies()
    {
        _Cookies = new CookieContainer();
        _Cookie = new Cookie();
    }


    /// <summary>
    /// Deletes all cookies associated with a specified URL.
    /// </summary>
    /// <param name="URL">Required. The specified URL's cookies to clear.</param>
    public void ClearCookies(string URL)
    {
        try
        {
            CookieCollection CC = _Cookies.GetCookies(new Uri(URL));
            foreach (Cookie Cookie in CC)
            {
                Cookie.Expired = true;
            }
        }
        catch (Exception ex)
        {
            ExceptionHandler(ex);
        }
    }


    /// <summary>
    /// Retrieves all cookies stored in the wrapper and returns them in a CookieCollection.
    /// </summary>
    /// <returns>A CookieCollection instance containing all the cookies in the wrapper.</returns>
    public CookieCollection GetAllCookies()
    {
        CookieCollection lstCookies = new CookieCollection();
        Hashtable table = (Hashtable)_Cookies.GetType().InvokeMember("m_domainTable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.GetField | System.Reflection.BindingFlags.Instance, null, _Cookies, new object[] { });
        foreach (object pathList in table.Values)
        {
            SortedList lstCookieCol = (SortedList)pathList.GetType().InvokeMember("m_list", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.GetField | System.Reflection.BindingFlags.Instance, null, pathList, new object[] { });
            foreach (CookieCollection colCookies in lstCookieCol.Values)
                foreach (Cookie c in colCookies)
                {
                    lstCookies.Add(c);
                }
        }
        return lstCookies;
    }


    /// <summary>
    /// Returns the HTTP cookie headers that contain the HTTP cookies that represent the System.Net.Cookie instances that are associated with the specified URL.
    /// </summary>
    /// <param name="URL">Required. The URL of the cookies desired.</param>
    /// <returns></returns>
    public string GetCookieString(string URL)
    {
        try
        {
            return _Cookies.GetCookieHeader(new Uri(URL));
        }
        catch (Exception ex)
        {
            ExceptionHandler(ex);
            return string.Empty;
        }
    }


    /// <summary>
    /// Returns System.Net.CookieCollection that contains the System.Net.Cookie instances that associate with the specified URL
    /// </summary>
    /// <param name="URL">Required. The URL of the cookies desired.</param>
    /// <returns></returns>
    public CookieCollection GetCookieCollection(string URL)
    {
        try
        {
            return _Cookies.GetCookies(new Uri(URL));
        }
        catch (Exception ex)
        {
            ExceptionHandler(ex);
            return null;
        }
    }


    /// <summary>
    /// Adds and associates a specified cookie with a URL in the wrapper's CookieCollection.
    /// </summary>
    /// <param name="URL">Required. The URL to associate the cookie with.</param>
    /// <param name="Cookie">Required. The cookie to add to the CookieCollection.</param>
    public void AddCookie(string URL, Cookie Cookie)
    {
        try
        {
            _Cookies.Add(new Uri(URL), Cookie);
        }
        catch (Exception ex)
        {
            ExceptionHandler(ex);
        }
    }


    /// <summary>
    /// Adds and associates an array of cookies with a URL in the wrapper's CookieCollection.
    /// </summary>
    /// <param name="URL">Required. The URL to associate all cookies with.</param>
    /// <param name="Cookie">Required. The array of cookies to add to the CookieCollection.</param>
    public void AddCookieArray(string URL, Cookie[] Cookie)
    {
        try
        {
            foreach (Cookie c in Cookie)
            {
                _Cookies.Add(new Uri(URL), c);
            }
        }
        catch (Exception ex)
        {
            ExceptionHandler(ex);
        }
    }


    /// <summary>
    /// Sets and associates a specified CookieCollection with a URL in the wrapper's CookieCollection.
    /// </summary>
    /// <param name="URL">Required. The URL to associate the CookieCollection with.</param>
    /// <param name="CCollection">Required. The CookieCollection to add to the wrapper's CookieCollection.</param>
    public void AddCookieCollection(string URL, CookieCollection CCollection)
    {
        try
        {
            _Cookies.Add(new Uri(URL), CCollection);
        }
        catch (Exception ex)
        {
            ExceptionHandler(ex);
        }
    }


    /// <summary>
    /// Adds and associates System.Net.Cookie instances from an HTTP cookie header to the wrapper's CookieCollection with a specific URL.
    /// </summary>
    /// <param name="URL">Required. The specific URL to associate the cookie instances with.</param>
    /// <param name="CookieString">Required. The string of cookies or cookie header to add.</param>
    public void AddCookieString(string URL, string CookieString)
    {
        string[] strCookies = CookieString.Split(new string[] { "; " }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string strCookie in strCookies)
        {
            _Cookies.SetCookies(new Uri(URL), strCookie);
        }
    }


    /// <summary>
    /// Clones the wrapper's cookies of a specific domain and associates them with a new domain.
    /// </summary>
    /// <param name="OldDomain">Required. The old domain to clone cookies from.</param>
    /// <param name="NewDomain">Required. The new domain to clone cookies to.</param>
    /// <returns></returns>
    public bool CloneCookies(string OldDomain, string NewDomain)
    {
        try
        {
            return CloneCookies(new Uri(OldDomain), new Uri(NewDomain));
        }
        catch (Exception ex)
        {
            ExceptionHandler(ex);
            return false;
        }
    }


    /// <summary>
    /// Clones the wrapper's cookies of a specific domain and associates them with a new domain.
    /// </summary>
    /// <param name="OldDomain">Required. The old domain to clone cookies from.</param>
    /// <param name="NewDomain">Required. The new domain to clone cookies to.</param>
    /// <returns></returns>
    public bool CloneCookies(Uri OldDomain, Uri NewDomain)
    {
        try
        {
            string CookNewStr = string.Empty;
            foreach (System.Net.Cookie Cook in _Cookies.GetCookies(OldDomain))
            {
                _Cookies.SetCookies(NewDomain, Cook.Name + "=" + Cook.Value + ((Cook.Expires != null) ? "; expires=" + Cook.Expires.ToString() : "") + (!(Cook.Path == string.Empty) ? "; path=" + Cook.Path : "" + "; domain=") + NewDomain.Host + (Cook.HttpOnly ? "; HttpOnly" : ""));
            }
            return true;
        }
        catch (Exception ex)
        {
            ExceptionHandler(ex);
            return false;
        }
    }


    /// <summary>
    /// Generates and returns a specified length of random alphabetical string. Optional ignore casing and additional charaters.
    /// </summary>
    /// <param name="intLength">Required. Specifies the length of random string to return.</param>
    /// <param name="UseCaps">Optional. Indicates whether upper casing will be used.</param>
    /// <param name="AdditionalCharacters">Optional. String of additional characters to include in randomization.</param>
    /// <returns>A return value of the random generated string.</returns>
    /// <remarks></remarks>
    private string RandomString(int intLength, bool UseCaps = false, string AdditionalCharacters = "")
    {
        const string ALPHA = "abcdefghijklmnopqrstuvwxyz";
        string strGenerate = string.Empty;
        if (UseCaps) strGenerate = ALPHA + ALPHA.ToUpper() + AdditionalCharacters + AdditionalCharacters;
        else strGenerate = ALPHA + AdditionalCharacters;

        return RandomString(strGenerate, intLength);
    }


    /// <summary>
    /// Generates and returns a specified length of random string based on given string to select from.
    /// </summary>
    /// <param name="strSelect">Required. The string's characters to select from.</param>
    /// <param name="intLength">Required. The length of the string to return.</param>
    /// <returns></returns>
    private string RandomString(string strSelect, int intLength)
    {
        return RandomString(strSelect.ToCharArray(), intLength);
    }


    /// <summary>
    /// Generates and returns a specified length of random string based on given char array to select from.
    /// </summary>
    /// <param name="chrSelect">Required. The char array to select from.</param>
    /// <param name="intLength">Required. The length of the string to return.</param>
    /// <returns></returns>
    private string RandomString(char[] chrSelect, int intLength)
    {
        if (intLength < 1 || chrSelect.Length < 1) return string.Empty;

        Random rng = new Random();
        StringBuilder sbOut = new StringBuilder(intLength);

        while (!(sbOut.Length == intLength))
        {
            int intRand = rng.Next(0, chrSelect.Length - 1);

            sbOut.Append(chrSelect[intRand]);
        }

        return sbOut.ToString();
    }


    private string ReadFile(string Filename)
    {
        try
        {
            if (File.Exists(Filename))
            {
                FileStream FS = new FileStream(Filename, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (FS.Length > 0)
                {
                    byte[] readBytes = new byte[Convert.ToInt32(FS.Length - 1) + 1];
                    FS.Read(readBytes, 0, readBytes.Length);
                    FS.Close();
                    return Encoding.Default.GetString(readBytes);
                }
            }
        }
        catch (Exception ex)
        {
            ExceptionCatcher(this, ex);
        }
        return string.Empty;
    }


    private string StandardizeURL(string s, bool blnHTTP = false)
    {
        Regex regWWW = new Regex(@"www.*?\.");

        string strReturn = regWWW.Replace(s.Trim(), string.Empty, 1);

        if (strReturn.StartsWith(".")) strReturn = strReturn.Remove(0, 1);

        if (blnHTTP && !strReturn.StartsWith("http://")) strReturn = "http://" + strReturn;

        if (strReturn.EndsWith("/")) strReturn = strReturn.Remove(strReturn.Length - 1);

        return strReturn;
    }


    private void ExceptionHandler(Exception Ex)
    {
        if ((ExceptionCatcher != null))
        {
            ExceptionCatcher.BeginInvoke(this, Ex, null, null);
        }
    }


    /// <summary>
    /// Clones and returns a new instance of the HTTPWrapper.
    /// </summary>
    /// <returns></returns>
    public object Clone()
    {
        return (HTTPWrapper)MemberwiseClone();
    }


    #endregion


    #region Requests

    /// <summary>
    /// Initiates a request to a specified URL using "GET" headers.
    /// </summary>
    /// <param name="strURL">Required. The Uniform Resource Locator to request.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns>The data returned by the request.</returns>
    public Result<string> GetRequest(string strURL, bool setLastPage = true)
    {
        return GetRequest(strURL, string.Empty, setLastPage);
    }


    /// <summary>
    /// Initiates a request to a specified URL using "GET" headers.
    /// </summary>
    /// <param name="strURL">Required. The Uniform Resource Locator to request.</param>
    /// <param name="strReferer">Required. The URL to send as the last page visited.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns>The data returned by the request.</returns>
    public Result<string> GetRequest(string strURL, string strReferer, bool setLastPage = true)
    {
        return Request("GET", strURL, strReferer);
    }


    /// <summary>
    /// Initiates a request to a specified URL and POST data using "POST" headers.
    /// </summary>
    /// <param name="strURL">Required. The Uniform Resource Locator to request.</param>
    /// <param name="strPostData">Required. The data to send along with POST request.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns></returns>
    public Result<string> PostRequest(string strURL, string strPostData, bool setLastPage = true)
    {
        return PostRequest(strURL, strPostData, string.Empty, setLastPage);
    }


    /// <summary>
    /// Initiates a request to a specified URL and POST data using "POST" headers.
    /// </summary>
    /// <param name="strURL">Required. The Uniform Resource Locator to request.</param>
    /// <param name="strPostData">Required. The data to send along with POST request.</param>
    /// <param name="strReferer">Required. The URL to send as the last page visited.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns></returns>
    public Result<string> PostRequest(string strURL, string strPostData, string strReferer, bool setLastPage = true)
    {
        return Request("POST", strURL, strPostData, strReferer, setLastPage);
    }


    /// <summary>
    /// Initiates a custom request to a specified URL with a custom method and optional parameters.
    /// </summary>
    /// <param name="strMethod">Required. The header method to be used in the request.</param>
    /// <param name="strURL">Required. The Uniform Resource Locator to request.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns></returns>
    public Result<string> Request(string strMethod, string strURL, bool setLastPage = true)
    {
        return Request(strMethod, strURL, string.Empty, string.Empty);
    }


    /// <summary>
    /// Initiates a custom request to a specified URL with a custom method and optional parameters.
    /// </summary>
    /// <param name="strMethod">Required. The header method to be used in the request.</param>
    /// <param name="strURL">Required. The Uniform Resource Locator to request.</param>
    /// <param name="strReferer">Required. The URL to send as the last page visited.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns></returns>
    public Result<string> Request(string strMethod, string strURL, string strReferer, bool setLastPage = true)
    {
        return Request(strMethod, strURL, string.Empty, strReferer, setLastPage);
    }


    /// <summary>
    /// Initiates a custom request to a specified URL with a custom method and optional parameters.
    /// </summary>
    /// <param name="strMethod">Required. The header method to be used in the request.</param>
    /// <param name="strURL">Required. The Uniform Resource Locator to request.</param>
    /// <param name="strPostData">Required. Data to be sent along with the URL.</param>
    /// <param name="strReferer">Required. The URL to send as the last page visited.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns></returns>
    public Result<string> Request(string strMethod, string strURL, string strPostData, string strReferer, bool setLastPage = true)
    {
        AsyncRequest<string> asyncTask = RequestAsync(strMethod, strURL, strPostData, strReferer, setLastPage);

        asyncTask.Task.Wait(_Timeout);

        // Stop process
        if (!asyncTask.Task.IsCompleted) asyncTask.CancelToken.Cancel();

        return asyncTask.Task.Result;
    }


    private bool GetString(CancellationToken cancel, out string strResult, Stream strmRead)
    {
        strResult = null;

        if (strmRead == null)
            return false;


        StringBuilder strBuilder = new StringBuilder();
        int intRead;
        char[] buffer = new char[_BufferSize];

        try
        {
            using (StreamReader wReader = new StreamReader(strmRead, _Encoding))
            {
                cancel.ThrowIfCancellationRequested();
                while ((intRead = wReader.ReadBlock(buffer, 0, _BufferSize)) != 0)
                {
                    strBuilder.Append(buffer, 0, intRead);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (strmRead != null)
                strmRead.Close();

            return false;
        }
        catch (Exception ex)
        {
            if (strmRead != null)
                strmRead.Close();

            ExceptionHandler(ex);
            return false;
        }

        if (strmRead != null)
            strmRead.Close();

        strResult = strBuilder.ToString();
        return true;
    }


    /// <summary>
    /// Initiates a request to the URL of an image and downloads it as a Bitmap.
    /// </summary>
    /// <param name="strURL">Required. The Uniform Resource Locator of the image to request.</param>
    /// <param name="intTimeout">Optional. The maximum time to wait for request before terminating. If not set, the wrapper's timeout value will be used.</param>
    /// <param name="strReferer">Optional. The URL to send as the last page visited.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns>Returns downloaded Bitmap if successful or null if unsuccessful.</returns>
    public Result<Bitmap> DownloadImage(string strURL, int intTimeout = -1, string strReferer = "", bool setLastPage = false)
    {
        AsyncRequest<Bitmap> asyncTask = DownloadImageAsync(strURL, intTimeout, strReferer, setLastPage);

        asyncTask.Task.Wait(_Timeout);

        // Stop process
        if (!asyncTask.Task.IsCompleted) asyncTask.CancelToken.Cancel();

        return asyncTask.Task.Result;
    }


    /// <summary>
    /// Downloads and saves a file to a specified location on disk.
    /// </summary>
    /// <param name="strURL">Required. The Uniform Resource Locator at which to download as a file.</param>
    /// <param name="FileLocation">Required. The location on disk to save the downloaded file to.</param>
    /// <param name="strReferer">Optional. The URL to send as the last page visited.</param>
    /// <returns></returns>
    public bool DownloadFile(string strURL, string FileLocation, string strReferer = "", int intBufferSize = 8192)
    {
        object response = Go(new CancellationToken(), "GET", strURL, strReferer, false);

        if (!(response is Stream))
            return false;

        try
        {
            string strHTML = string.Empty;
            using (Stream strmResponse = (Stream)response)
            {
                using (FileStream file = new FileStream(FileLocation, FileMode.Create, FileAccess.Write, FileShare.Write))
                {
                    byte[] buffer = new byte[intBufferSize];
                    int intRead = 0;
                    while ((intRead = strmResponse.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        file.Write(buffer, 0, intRead);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ExceptionHandler(ex);
        }
        return false;
    }


    /// <summary>
    /// Downloads and saves a file to disk from the internet using the Download Manager to monitor progress, speed, and information.
    /// </summary>
    /// <param name="DLState">Required. The Download Manager class to use.</param>
    public void DownloadFile(DownloadManager DLState)
    {
        HttpWebResponse wResponse = null;
        Stream wStream = null;
        FileStream fStream = null;
        try
        {
            HttpWebRequest wRequest = (HttpWebRequest)WebRequest.Create(DLState.URL);
            wRequest.CookieContainer = _Cookies;
            wRequest.Method = "GET";
            if (!(DLState.Referer == string.Empty))
            {
                wRequest.Headers.Add("Referer", DLState.Referer);
            }
            wRequest.Timeout = DLState.Timeout;
            wRequest.UserAgent = (!(_UserAgent == "") ? conUserAgent : _UserAgent);
            wRequest.Pipelined = _UsePipelining;
            DLState.WebRequest = wRequest;
            wResponse = (HttpWebResponse)wRequest.GetResponse();
            DLState.Length = Convert.ToInt64(wResponse.ContentLength);
            wStream = wResponse.GetResponseStream();
            fStream = new FileStream(DLState.FileLocation, FileMode.Create, FileAccess.Write, FileShare.Write);

            DLState.StartTime = DateTime.Now;
            int Readings = 0;

            while (!DLState.Cancelled)
            {
                DLState.SpeedTimer.Start();

                byte[] readBytes = new byte[DLState.BufferSize];
                DLState.ReadWrite = wStream.Read(readBytes, 0, readBytes.Length);
                DLState.TotalReadWrite += DLState.ReadWrite;

                if (DLState.ReadWrite == 0)
                    break;
                fStream.Write(readBytes, 0, DLState.ReadWrite);

                DLState.SpeedTimer.Stop();

                if (!DLState.Cancelled)
                {
                    DLState.Callback.Invoke(DLState);
                }

                Readings += 1;

                if (Readings > DLState.SpeedUpdateRate)
                {
                    DLState.CalcSpeed();
                    Readings -= DLState.SpeedUpdateRate;
                }
            }
            DLState.EndTime = DateTime.Now;
            DLState.Finished = true;
            DLState.Success = (DLState.Length == DLState.TotalReadWrite);
            fStream.Close();
            wStream.Close();
            wResponse.Close();
            DLState.WebRequest = null;
            DLState.Callback.Invoke(DLState);
            //Return True
        }
        catch (WebException rex)
        {
            if (!(rex.Status == WebExceptionStatus.RequestCanceled))
            {
                ExceptionHandler(rex);
            }
        }
        catch (Exception ex)
        {
            ExceptionHandler(ex);
        }
        if ((fStream != null))
        {
            fStream.Close();
        }
        if ((wStream != null))
        {
            wStream.Close();
        }
        if ((wResponse != null))
        {
            wResponse.Close();
        }
        DLState.Finished = true;
        DLState.WebRequest = null;
        DLState.Callback.Invoke(DLState);
        //Return False
    }


    /// <summary>
    /// Uploads a file from disk to the internet using the Upload Manager to monitor progress, speed, and information.
    /// </summary>
    /// <param name="ULState"></param>
    public void UploadFile(UploadManager ULState)
    {
        HttpWebResponse wResponse = null;
        Stream wReqStream = null;
        Stream wResStream = null;
        HttpWebRequest wRequest = null;
        try
        {
            if (ULState.PostData == string.Empty)
            {
                int intPos = ULState.URL.IndexOf("?");
                if (intPos != -1)
                {
                    ULState.PostData = ULState.URL.Substring(intPos + 1);
                }
                if (!string.IsNullOrEmpty(ULState.PostData))
                {
                    ULState.URL = ULState.URL.Substring(0, ULState.URL.IndexOf(ULState.PostData) - 1);
                }
            }
            string boundary = string.Empty;
            string postdata = GenerateMultipart(ULState.PostData, ref boundary);
            wRequest = (HttpWebRequest)WebRequest.Create(ULState.URL);
            wRequest.CookieContainer = _Cookies;
            if (!(ULState.Referer == string.Empty))
            {
                wRequest.Referer = ULState.Referer;
            }
            ULState.WebRequest = wRequest;
            ULState.Length = Convert.ToInt64(postdata.Length);
            wRequest.Timeout = ULState.Timeout;
            wRequest.Method = "POST";
            wRequest.ContentType = "multipart/form-data; boundary=" + boundary;
            wRequest.ContentLength = postdata.Length;
            wReqStream = wRequest.GetRequestStream();

            byte[] reqData = Encoding.Default.GetBytes(postdata);

            ULState.StartTime = DateTime.Now;
            int Writings = 0;
            ULState.ReadWrite = ULState.BufferSize;

            while (!ULState.Cancelled)
            {
                ULState.SpeedTimer.Start();

                if (ULState.BufferSize > ULState.Length - ULState.TotalReadWrite)
                {
                    ULState.ReadWrite = Convert.ToInt32(ULState.Length - ULState.TotalReadWrite);
                }
                if (ULState.ReadWrite == 0) { break; }


                wReqStream.Write(reqData, Convert.ToInt32(ULState.TotalReadWrite), ULState.ReadWrite);
                ULState.TotalReadWrite += ULState.ReadWrite;

                ULState.SpeedTimer.Stop();

                if (!ULState.Cancelled)
                {
                    ULState.Callback.Invoke(ULState);
                }

                Writings += 1;

                if (Writings > ULState.SpeedUpdateRate)
                {
                    ULState.CalcSpeed();
                    Writings -= ULState.SpeedUpdateRate;
                }
            }
            ULState.EndTime = DateTime.Now;
            ULState.Finished = true;
            ULState.Success = (ULState.Length == ULState.TotalReadWrite);
            wReqStream.Close();

            wResponse = (HttpWebResponse)wRequest.GetResponse();
            wResStream = wResponse.GetResponseStream();

            byte[] readResponse = new byte[Convert.ToInt32(wResponse.ContentLength - 1) + 1];
            int readLen = 0;
            int readSize = ULState.BufferSize;

            while (!ULState.Cancelled)
            {
                if (readSize > wResponse.ContentLength - readLen)
                {
                    readSize = Convert.ToInt32(wResponse.ContentLength - readLen);
                }
                ULState.ReadWrite = wResStream.Read(readResponse, readLen, readSize);

                if (ULState.ReadWrite == 0)
                    break; // TODO: might not be correct. Was : Exit Do
                readLen += ULState.ReadWrite;
            }
            ULState.Response = Encoding.Default.GetString(readResponse);
            wResStream.Close();
            wResponse.Close();
            ULState.GotResponse = true;
            ULState.WebRequest = null;
            ULState.Callback.Invoke(ULState);
            //Return True
        }
        catch (WebException rex)
        {
            if (!(rex.Status == WebExceptionStatus.RequestCanceled))
            {
                ExceptionHandler(rex);
            }
        }
        catch (Exception ex)
        {
            ExceptionHandler(ex);
            if ((wReqStream != null))
            {
                wReqStream.Close();
            }
        }
        if ((wResStream != null))
        {
            wResStream.Close();
        }
        if ((wResponse != null))
        {
            wResponse.Close();
        }
        ULState.Finished = true;
        ULState.WebRequest = null;
        ULState.Callback.Invoke(ULState);
        //Return False
    }


    public HttpWebRequest SetupRequest(string strMethod, string strURL, string strReferer, bool setLastPage, string strData = null)
    {
        HttpWebRequest request;

        try
        {
            request = (HttpWebRequest)WebRequest.Create(strURL);
        }
        catch (Exception ex)
        {
            ExceptionHandler(ex);
            return null;
        }

        try
        {
            request.Proxy = (_UseProxy ? _Proxy : null);
        }
        catch (Exception ex)
        {
            ExceptionHandler(ex);
            return null;
        }

        request.CachePolicy = (_UseCaching ? new HttpRequestCachePolicy(HttpRequestCacheLevel.Revalidate) : new HttpRequestCachePolicy(HttpRequestCacheLevel.NoCacheNoStore));


        if (_UseGzip)
        {
            try
            {
                request.AutomaticDecompression = DecompressionMethods.GZip;
                request.Headers.Add(HttpRequestHeader.AcceptEncoding, "gzip,deflate");
            }
            catch (InvalidOperationException ex)
            {
                ExceptionHandler(ex);
            }
        }

        try
        {
            if (strMethod == "PIC")
            {
                request.Accept = "image/png,*/*;q=0.5";
                request.Method = "GET";
            }
            else
            {
                request.Accept = "text/xml,application/xml,application/xhtml+xml,text/html;q=0.9,text/plain;q=0.8,image/png,*/*;q=0.5";
                request.Method = strMethod;
            }
        }
        catch (ArgumentException ex)
        {
            ExceptionHandler(ex);
        }


        request.Referer = strReferer;
        request.CookieContainer = _Cookies;
        request.UserAgent = (_UserAgent == string.Empty ? conUserAgent : _UserAgent);
        request.Pipelined = (strMethod != "POST" && _UsePipelining);


        if (setLastPage) { _LastPage = strURL; }


        if (strData != null && !strData.Equals(string.Empty))
        {
            try
            {
                byte[] byData = _Encoding.GetBytes(strData);
                request.ContentType = "application/x-www-form-urlencoded";
                request.ContentLength = byData.Length;

                using (Stream PostData = request.GetRequestStream())
                {
                    PostData.Write(byData, 0, byData.Length);
                }
            }
            catch (Exception ex)
            {
                ExceptionHandler(ex);
                return null;
            }
        }

        return request;
    }


    private object Go(CancellationToken cancel, string strMethod, string strURL, string strReferer, bool setLastPage, string strData = null)
    {
       return Go(cancel, SetupRequest(strMethod, strURL, strReferer, setLastPage, strData));
    }


    private object Go(CancellationToken cancel, HttpWebRequest request)
    {
        Stream strmResponse = null;

        if (request == null) return null;

        IAsyncResult asyncResult = null;

        if (cancel.IsCancellationRequested)
            return new OperationCanceledException();

        try
        {
            asyncResult = request.BeginGetResponse(null, request);

            while (!asyncResult.IsCompleted)
            {
                cancel.ThrowIfCancellationRequested();
            }

            strmResponse = (request.EndGetResponse(asyncResult)).GetResponseStream();
        }
        catch (OperationCanceledException ex)
        {
            request.Abort();
            return ex;
        }
        catch (Exception ex)
        {
            ExceptionHandler(ex);
            return ex;
        }
        return strmResponse;
    }


    private string GenerateMultipart(string strPost, ref string boundary)
    {
        if (boundary == string.Empty)
        {
            boundary = RandomString(30, true, "0123456789").PadLeft(42, Convert.ToChar("-"));
        }
        string postData = string.Empty;
        MatchCollection PostKVP = Regex.Matches(strPost, "([^&=]+)=([^&=]+)", RegexOptions.IgnoreCase);
        string FileName = string.Empty;

        foreach (Match kvp in PostKVP)
        {
            if (kvp.Groups[1].Value.StartsWith("{FILE}") | kvp.Groups[1].Value.StartsWith("{FILEX}"))
            {
                FileName = kvp.Groups[2].Value;
                if (kvp.Groups[1].Value.StartsWith("{FILEX}"))
                {
                    FileName = Uri.UnescapeDataString(FileName);
                }
                postData += "--" + boundary + Environment.NewLine + "Content-Disposition: form-data; name=\"" + kvp.Groups[1].Value.Substring(kvp.Groups[1].Value.StartsWith("{FILEX}") ? "{FILEX}".Length : "{FILE}".Length) + "\"; filename=\"" + Path.GetFileName(FileName) + "\"" + Environment.NewLine + "Content-Type: application/octet-stream" + Environment.NewLine + Environment.NewLine + ReadFile(FileName) + Environment.NewLine;
            }
            else
            {
                postData += "--" + boundary + Environment.NewLine + "Content-Disposition: form-data; name=\"" + kvp.Groups[1].Value + "\"" + Environment.NewLine + Environment.NewLine + kvp.Groups[2].Value + Environment.NewLine;
            }
        }
        postData += "--" + boundary + "--" + Environment.NewLine;

        return postData;
    }

    #endregion


    #region Async Requests

    /// <summary>
    /// Initiates a request to a specified URL using "GET" headers.
    /// </summary>
    /// <param name="strURL">Required. The Uniform Resource Locator to request.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns>The data returned by the request.</returns>
    public AsyncRequest<string> GetRequestAsync(string strURL, bool setLastPage = true)
    {
        return GetRequestAsync(strURL, string.Empty, setLastPage);
    }


    /// <summary>
    /// Initiates a request to a specified URL using "GET" headers.
    /// </summary>
    /// <param name="strURL">Required. The Uniform Resource Locator to request.</param>
    /// <param name="strReferer">Required. The URL to send as the last page visited.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns>The data returned by the request.</returns>
    public AsyncRequest<string> GetRequestAsync(string strURL, string strReferer, bool setLastPage = true)
    {
        return RequestAsync("GET", strURL, strReferer);
    }


    /// <summary>
    /// Initiates a request to a specified URL and POST data using "POST" headers.
    /// </summary>
    /// <param name="strURL">Required. The Uniform Resource Locator to request.</param>
    /// <param name="strPostData">Required. The data to send along with POST request.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns></returns>
    public AsyncRequest<string> PostRequestAsync(string strURL, string strPostData, bool setLastPage = true)
    {
        return PostRequestAsync(strURL, strPostData, string.Empty, setLastPage);
    }


    /// <summary>
    /// Initiates a request to a specified URL and POST data using "POST" headers.
    /// </summary>
    /// <param name="strURL">Required. The Uniform Resource Locator to request.</param>
    /// <param name="strPostData">Required. The data to send along with POST request.</param>
    /// <param name="strReferer">Required. The URL to send as the last page visited.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns></returns>
    public AsyncRequest<string> PostRequestAsync(string strURL, string strPostData, string strReferer, bool setLastPage = true)
    {
        return RequestAsync("POST", strURL, strPostData, strReferer, setLastPage);
    }


    /// <summary>
    /// Initiates a custom request to a specified URL with a custom method and optional parameters.
    /// </summary>
    /// <param name="strMethod">Required. The header method to be used in the request.</param>
    /// <param name="strURL">Required. The Uniform Resource Locator to request.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns></returns>
    public AsyncRequest<string> RequestAsync(string strMethod, string strURL, bool setLastPage = true)
    {
        return RequestAsync(strMethod, strURL, string.Empty, string.Empty);
    }


    /// <summary>
    /// Initiates a custom request to a specified URL with a custom method and optional parameters.
    /// </summary>
    /// <param name="strMethod">Required. The header method to be used in the request.</param>
    /// <param name="strURL">Required. The Uniform Resource Locator to request.</param>
    /// <param name="strReferer">Required. The URL to send as the last page visited.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns></returns>
    public AsyncRequest<string> RequestAsync(string strMethod, string strURL, string strReferer, bool setLastPage = true)
    {
        return RequestAsync(strMethod, strURL, string.Empty, strReferer, setLastPage);
    }


    /// <summary>
    /// Initiates a custom request to a specified URL with a custom method and optional parameters.
    /// </summary>
    /// <param name="strMethod">Required. The header method to be used in the request.</param>
    /// <param name="strURL">Required. The Uniform Resource Locator to request.</param>
    /// <param name="strPostData">Required. Data to be sent along with the URL.</param>
    /// <param name="strReferer">Required. The URL to send as the last page visited.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns></returns>
    public AsyncRequest<string> RequestAsync(string strMethod, string strURL, string strPostData, string strReferer, bool setLastPage = true)
    {
        CancellationTokenSource cancel = new CancellationTokenSource();

        Task<Result<string>> taskGo = new Task<Result<string>>((obj) =>
        {
            Stopwatch stopwatch = new Stopwatch();

            // Start timer
            stopwatch.Start();

            string strResult = null;

            if (cancel.IsCancellationRequested)
            {
                stopwatch.Stop();
                return new Result<string>(null, false, stopwatch.ElapsedMilliseconds);
            }

            Stream strmResponse;
            {
                object tmp = Go(cancel.Token, strMethod, strURL, strReferer, setLastPage, strPostData);

                stopwatch.Stop();

                if (tmp == null)
                    return new Result<string>(null, false, stopwatch.ElapsedMilliseconds);
                else if (tmp is Stream)
                {
                    stopwatch.Start();
                    strmResponse = (Stream)tmp;
                }
                else if (tmp is Exception)
                    return new Result<string>(null, false, stopwatch.ElapsedMilliseconds, (Exception)tmp);
                else
                    return new Result<string>(null, false, stopwatch.ElapsedMilliseconds);
            }

            if (cancel.IsCancellationRequested)
            {
                stopwatch.Stop();
                if (strmResponse != null) strmResponse.Dispose();
                return new Result<string>(null, false, stopwatch.ElapsedMilliseconds);
            }

            bool success = GetString(cancel.Token, out strResult, strmResponse);

            stopwatch.Stop();
            return new Result<string>(strResult, success, stopwatch.ElapsedMilliseconds);
        }, cancel, TaskCreationOptions.LongRunning);


        // Start process
        taskGo.Start();

        return new AsyncRequest<string>(taskGo, cancel);
    }


    /// <summary>
    /// Initiates a request to the URL of an image and downloads it as a Bitmap.
    /// </summary>
    /// <param name="strURL">Required. The Uniform Resource Locator of the image to request.</param>
    /// <param name="intTimeout">Optional. The maximum time to wait for request before terminating. If not set, the wrapper's timeout value will be used.</param>
    /// <param name="strReferer">Optional. The URL to send as the last page visited.</param>
    /// <param name="setLastPage">Optional. Determines whether to set strURL as LastPage property.</param>
    /// <returns>Returns downloaded Bitmap if successful or null if unsuccessful.</returns>
    public AsyncRequest<Bitmap> DownloadImageAsync(string strURL, int intTimeout = -1, string strReferer = "", bool setLastPage = false)
    {
        CancellationTokenSource cancel = new CancellationTokenSource();

        Task<Result<Bitmap>> taskGo = new Task<Result<Bitmap>>((obj) =>
        {
            Result<Bitmap> result = new Result<Bitmap>(null, false, 0);

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            Bitmap bmpResult = null;
            Stream strmResponse;

            {
                object tmp = Go(cancel.Token, "PIC", strURL, strReferer, setLastPage);

                stopwatch.Stop();

                if (tmp == null)
                    return new Result<Bitmap>(null, false, stopwatch.ElapsedMilliseconds);
                else if (tmp is Stream)
                {
                    stopwatch.Start();
                    strmResponse = (Stream)tmp;
                }
                else if (tmp is Exception)
                    return new Result<Bitmap>(null, false, stopwatch.ElapsedMilliseconds, (Exception)tmp);
                else
                    return new Result<Bitmap>(null, false, stopwatch.ElapsedMilliseconds);
            }

            try
            {
                bmpResult = new Bitmap(strmResponse);
            }
            catch (Exception ex)
            {
                ExceptionHandler(ex);
                
            }

            stopwatch.Stop();

            if (strmResponse != null)
                strmResponse.Dispose();

            return new Result<Bitmap>(bmpResult, bmpResult != null, stopwatch.ElapsedMilliseconds);
        }, cancel, TaskCreationOptions.LongRunning);


        // Start process
        taskGo.Start();

        return new AsyncRequest<Bitmap>(taskGo, cancel);
    }

    #endregion


    #region Classes

    internal class Result<T>
    {
        #region Members
        protected T _Value;
        protected bool _Success;
        protected long _RequestTime;
        protected Exception _ExceptionThrown;
        #endregion

        /// <summary>
        /// Initializes a new HTTPResult with given result values.
        /// </summary>
        /// <param name="value">Required. The value of an HTTPWrapper request.</param>
        /// <param name="success">Required. Whether the request was successful.</param>
        /// <param name="intRequestTime">Required. The amount of time the request executed for.</param>
        /// <param name="exceptionThrown">Optional. The exception thrown, if applicable.</param>
        public Result(T value, bool success, long intRequestTime, Exception exceptionThrown = null)
        {
            _Value = value;
            _Success = success;
            _RequestTime = intRequestTime;
            _ExceptionThrown = exceptionThrown;
        }

        public override string ToString()
        {
            if (_Value is string)
                return Value.ToString();
            else
                return base.ToString();
        }

        #region Properties

        /// <summary>
        /// Gets the result of an HTTPWrapper request.
        /// </summary>
        public T Value
        {
            get { return _Value; }
        }

        /// <summary>
        /// Gets whether the HTTPWrapper request was successful.
        /// </summary>
        public bool Success
        {
            get { return _Success; }
        }

        /// <summary>
        /// Gets the amount of time the HTTPWrapper request executed for.
        /// </summary>
        public long RequestTime
        {
            get { return _RequestTime; }
        }

        /// <summary>
        /// The exception thrown during the request, if applicable.
        /// </summary>
        public Exception ExceptionThrown
        {
            get { return _ExceptionThrown; }
        }



        #endregion
    }

    internal class AsyncRequest<T>
    {
        #region Members
        private Task<Result<T>> _Task;
        private CancellationTokenSource _Cancel;
        #endregion

        public AsyncRequest(Task<Result<T>> task, CancellationTokenSource cancel)
        {
            _Task = task;
            _Cancel = cancel;
        }

        #region Properties

        public Task<Result<T>> Task
        {
            get { return _Task; }
        }

        public CancellationTokenSource CancelToken
        {
            get { return _Cancel; }
        }

        #endregion

    }

    #endregion
}


