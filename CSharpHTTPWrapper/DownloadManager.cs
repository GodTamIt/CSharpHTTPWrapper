using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net;

internal class DownloadManager
{
    private string _URL = string.Empty;
    private string _Referer = string.Empty;
    private string _FileLocation = string.Empty;
    private HTTPWrapper.DownloadCallback _Callback = null;
    private int _BufferSize = 8192;
    private long _Length = 0;
    private int _ReadWrite = 0;
    private long _TotalReadWrite = 0;
    private int _Timeout = 120000;
    private DateTime _Start;
    private DateTime _End;
    static double _Speed = 0.0;
    private string _SpeedFormatted = _Speed + " bytes/s";
    private int _SpeedUpdateRate = 10;
    private int _SpeedLastTotalRead = 0;
    private Stopwatch _SpeedTimer = new Stopwatch();
    private DateTime _ETA = new DateTime();
    private bool _Cancelled = false;
    private bool _Finished = false;
    private bool _Success = false;
    private object _Identifier = null;

    private HttpWebRequest _WebRequest = null;
    public DownloadManager()
    {
        //Empty constructor
    }

    public DownloadManager(string URL, string FileLocation)
    {
        this.URL = URL;
        this.FileLocation = FileLocation;
    }

    public DownloadManager(string URL, string FileLocation, HTTPWrapper.DownloadCallback Callback)
        : this(URL, FileLocation)
    {
        this.Callback = Callback;
    }

    public DownloadManager(string URL, string FileLocation, HTTPWrapper.DownloadCallback Callback, int BufferSize)
        : this(URL, FileLocation, Callback)
    {
        this.BufferSize = BufferSize;
    }

    public string URL
    {
        get { return _URL; }
        set { _URL = value; }
    }

    public string Referer
    {
        get { return _Referer; }
        set { _Referer = value; }
    }

    public string FileLocation
    {
        get { return _FileLocation; }
        set { _FileLocation = value; }
    }

    public HTTPWrapper.DownloadCallback Callback
    {
        get { return _Callback; }
        set { _Callback = value; }
    }

    public int BufferSize
    {
        get { return _BufferSize; }
        set { _BufferSize = value; }
    }

    public long Length
    {
        get { return _Length; }
        set { _Length = value; }
    }

    public int ReadWrite
    {
        get { return _ReadWrite; }
        set { _ReadWrite = value; }
    }

    public long TotalReadWrite
    {
        get { return _TotalReadWrite; }
        set { _TotalReadWrite = value; }
    }

    public int Timeout
    {
        get { return _Timeout; }
        set { _Timeout = value; }
    }

    public DateTime StartTime
    {
        get { return _Start; }
        set { _Start = value; }
    }

    public DateTime EndTime
    {
        get { return _End; }
        set { _End = value; }
    }

    public double Speed
    {
        get { return _Speed; }
    }

    public string SpeedFormatted
    {
        get { return _SpeedFormatted; }
    }

    public int SpeedUpdateRate
    {
        get { return _SpeedUpdateRate; }
        set { _SpeedUpdateRate = value; }
    }

    public Stopwatch SpeedTimer
    {
        get { return _SpeedTimer; }
    }

    public DateTime ETA
    {
        get { return _ETA; }
    }

    public bool Cancelled
    {
        get { return _Cancelled; }
    }

    public bool Finished
    {
        get { return _Finished; }
        set { _Finished = value; }
    }

    public bool Success
    {
        get { return _Success; }
        set { _Success = value; }
    }

    public object Identifier
    {
        get { return _Identifier; }
        set { _Identifier = value; }
    }

    public HttpWebRequest WebRequest
    {
        set { _WebRequest = value; }
    }

    public void Cancel()
    {
        _Cancelled = true;
        if ((_WebRequest != null))
        {
            _WebRequest.Abort();
        }
    }

    public void CalcSpeed()
    {
        _Speed = (_TotalReadWrite - _SpeedLastTotalRead) / ((_SpeedTimer.ElapsedMilliseconds + 1) / 1000);
        if (_Speed > Math.Pow(1024, 3))
        {
            _SpeedFormatted = Math.Round(_Speed / Math.Pow(1024, 3), 2) + " gb/s";
        }
        else if (_Speed > Math.Pow(1024, 2))
        {
            _SpeedFormatted = Math.Round(_Speed / Math.Pow(1024, 2), 2) + " mb/s";
        }
        else
        {
            _SpeedFormatted = Math.Round(_Speed / 1024, 2) + " kb/s";
        }

        _ETA = new DateTime().AddSeconds((_Length - _TotalReadWrite) / _Speed);
        _SpeedLastTotalRead = Convert.ToInt32(_TotalReadWrite);
        _SpeedTimer.Reset();
    }
}

