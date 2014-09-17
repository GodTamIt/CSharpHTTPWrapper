using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net;

internal class UploadManager : DownloadManager
{

    private HTTPWrapper.UploadCallback _Callback = null;
    private string _PostData = string.Empty;
    private string _Response = string.Empty;

    private bool _GotResponse = false;
    public UploadManager(string URL)
    {
        this.URL = URL;
    }

    public UploadManager(string URL, string Postdata)
    {
        this.URL = URL;
        this.PostData = Postdata;
    }

    public UploadManager(string URL, string Postdata, HTTPWrapper.UploadCallback Callback)
        : this(URL, Postdata)
    {
        this.Callback = Callback;
    }

    public UploadManager(string URL, string Postdata, HTTPWrapper.UploadCallback Callback, int BufferSize)
        : this(URL, Postdata, Callback)
    {
        this.BufferSize = BufferSize;
    }

    public string PostData
    {
        get { return _PostData; }
        set { _PostData = value; }
    }

    public new HTTPWrapper.UploadCallback Callback
    {
        get { return _Callback; }
        set { _Callback = value; }
    }

    public string Response
    {
        get { return _Response; }
        set { _Response = value; }
    }

    public bool GotResponse
    {
        get { return _GotResponse; }
        set { _GotResponse = value; }
    }
}
