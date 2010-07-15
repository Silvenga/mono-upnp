// 
// FSpotContentDirectory.cs
//  
// Author:
//       Yavor Georgiev <fealebenpae@gmail.com>
// 
// Copyright (c) 2010 Yavor Georgiev
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Linq;
using Mono.Upnp.Dcp.MediaServer1.ContentDirectory1;
using Mono.Upnp.Dcp.MediaServer1.ContentDirectory1.Av;
using System.Net;
using System.Collections.Generic;
using Mono.Upnp.Xml;
using FSpot;
using System.Net.Sockets;
using FSpot.Query;

using FSpotPhoto = FSpot.Photo;
using UpnpPhoto = Mono.Upnp.Dcp.MediaServer1.ContentDirectory1.Av.Photo;
using UpnpObject = Mono.Upnp.Dcp.MediaServer1.ContentDirectory1.Object;
using System.Collections;

namespace Mono.Upnp.Dcp.MediaServer1.FSpot
{
    public class FSpotContentDirectory : ObjectBasedContentDirectory
    {
        bool started;
        HttpListener listener;
        string prefix = GeneratePrefix ();
        Db db = App.Instance.Database;

        Dictionary<uint, UpnpPhoto> photos_cache;
        Dictionary<uint, Container> tags_cache;

        List<uint> shared_tags;
        bool share_all_tags = true;

        public FSpotContentDirectory ()
        {
            photos_cache = new Dictionary<uint, UpnpPhoto> ();
            tags_cache = new Dictionary<uint, Container> ();

            LoadSharedTags ();

            PrepareRoot ();

            listener = new HttpListener { IgnoreWriteExceptions = true };
            listener.Prefixes.Add (prefix);
        }

        void LoadSharedTags ()
        {
            shared_tags = new List<uint> ();

            var client = new GConf.Client ();

            try {
                share_all_tags = (bool)client.Get (GConfConstants.SHARE_ALL_CATEGORIES_KEY);
                var list = client.Get (GConfConstants.SHARED_CATEGORIES_KEY);
                if (list != null) {
                    foreach (var item in (IEnumerable)list) {
                        shared_tags.Add (Convert.ToUInt32 (item));
                    }
                }
            } catch (GConf.NoSuchKeyException) {
            }
        }

        void PrepareRoot ()
        {
            var child_count = 0;
            if (!share_all_tags) {
                foreach (var tag in db.Tags.RootCategory.Children) {
                    if (shared_tags.Contains (tag.Id)) {
                        child_count++;
                    }
                }
            } else {
                child_count = db.Tags.RootCategory.Children.Count;
            }

            var root = new StorageFolder (this) {
                IsRestricted = true,
                Title = "F-Spot RootCategory",
                ChildCount = child_count
            };

            tags_cache.Add (db.Tags.RootCategory.Id, root);
        }

        public override void Start ()
        {
            CheckDisposed();

            if (started) {
                return;
            }

            base.Start ();

            started = true;

            lock (listener) {
                listener.Start ();
                listener.BeginGetContext (OnGotContext, null);
            }
        }

        public override void Stop ()
        {
            CheckDisposed ();

            if (!started) {
                return;
            }

            started = false;

            base.Stop ();

            lock (listener) {
                listener.Stop ();
            }
        }

        void OnGotContext (IAsyncResult result)
        {
            lock (listener) {
                if (!listener.IsListening) {
                    return;
                }

                var context = listener.EndGetContext (result);
                var query = context.Request.Url.Query;
                
                if (query.StartsWith ("?id="))
                {
                    ServePhoto (context.Response, query);
                } else
                {
                    context.Response.StatusCode = 404;
                }
                
                listener.BeginGetContext (OnGotContext, null);
            }
        }

        void ServePhoto (HttpListenerResponse response, string query)
        {
            var id = query.Substring (4);
            var photoId = photos_cache.Where ((kv) => kv.Value.Id == id).FirstOrDefault ().Key;

            var photo = db.Photos.Get (photoId);

            using (response) {
                if (photo == null) {
                    response.StatusCode = 404;
                    return;
                }

                try {
                    using (var reader = System.IO.File.OpenRead (photo.DefaultVersion.Uri.AbsolutePath)) {
                        response.ContentType = MimeTypeHelper.GetMimeType (photo.DefaultVersion.Uri);
                        response.ContentLength64 = reader.Length;
                        using (var stream = response.OutputStream) {
                            using (var writer = new System.IO.BinaryWriter (stream)) {
                                var buffer = new byte[8192];
                                int read;
                                do {
                                    read = reader.Read (buffer, 0, buffer.Length);
                                    writer.Write (buffer, 0, read);
                                } while (started && read > 0);
                            }
                        }
                    }
                } catch {
                }
            }
        }

        protected override void Dispose (bool disposing)
        {
            if (IsDisposed) {
                return;
            }
            
            base.Dispose (disposing);
            
            if (disposing) {
                Stop ();
                listener.Close ();
            }
            
            listener = null;
        }

        protected override string SearchCapabilities {
            get {
                return string.Empty;
            }
        }

        protected override string SortCapabilities {
            get {
                return string.Empty;
            }
        }

        protected override IEnumerable<IXmlSerializable> GetChildren (string objectId, int startIndex, int requestCount, string sortCriteria, out int totalMatches)
        {
            var tag_key_value = tags_cache.Where ((kv) => kv.Value.Id == objectId).FirstOrDefault ();
            if (tag_key_value.Value != null) {
                var tag = db.Tags.Get (tag_key_value.Key);
                if (tag != null) {
                    var results = db.Photos.Query (new TagTerm (tag));
                    totalMatches = results.Count ();

                    var upnp_result = new List<UpnpObject> ();

                    var category = tag as Category;
                    if (category != null) {
                        foreach (var child_tag in category.Children) {
                            if (!share_all_tags && !shared_tags.Contains (child_tag.Id)) {
                                continue;
                            }
                            upnp_result.Add (GetContainer (child_tag, tag_key_value.Value));
                            totalMatches++;
                        }
                    }

                    var photos = results.Skip (startIndex).Take (requestCount - totalMatches);

                    foreach (var photo in photos) {
                        upnp_result.Add (GetPhoto (photo, tag_key_value.Value));
                    }

                    Console.WriteLine (upnp_result.Count);

                    return upnp_result.Cast <IXmlSerializable> ();
                }
            }

            totalMatches = 0;
            return null;
        }

        UpnpPhoto GetPhoto (FSpotPhoto photo, Container parent)
        {
            UpnpPhoto upnp_photo = null;
            if (!photos_cache.ContainsKey (photo.Id)) {
                var photo_options = new PhotoOptions {
                    Title = photo.Name,
                    Rating = photo.Rating.ToString(),
                    Description = photo.Description
                };
                upnp_photo = new UpnpPhoto (photo_options, this, parent);

                var resource_settings = new ResourceSettings (new Uri (string.Format ("{0}object?id={1}", prefix, upnp_photo.Id))) {
                    ProtocolInfo = string.Format ("http-get:*:{0}:*", MimeTypeHelper.GetMimeType(photo.DefaultVersion.Uri))
                };
                upnp_photo.AddResource (new Resource (resource_settings));

                photos_cache.Add (photo.Id, upnp_photo);
            } else {
                upnp_photo = photos_cache [photo.Id];
            }

            return upnp_photo;
        }

        Container GetContainer (Tag tag, Container parent)
        {
            Container container = null;
            if (!tags_cache.ContainsKey (tag.Id)) {
                var photo_album_options = new PhotoAlbumOptions { Title = tag.Name, Description = "Tag" };
                var photo_album = new PhotoAlbum (photo_album_options, this, parent);
                tags_cache.Add (tag.Id, photo_album);
                container = photo_album;
            } else
            {
                container = tags_cache [tag.Id];
            }

            return container;
        }

        protected override IXmlSerializable GetObject (string objectId)
        {
            var tags = tags_cache.Values.Cast <UpnpObject> ();
            var photos = photos_cache.Values.Cast <UpnpObject> ();
            var objects = tags.Union (photos);

            var obj = objects.Where ((o) => o.Id == objectId).FirstOrDefault ();
            if (obj != null) {
                return obj;
            }

            return null;
        }

        public bool IsDisposed {
            get { return listener == null; }
        }

        void CheckDisposed ()
        {
            if (IsDisposed) {
                throw new ObjectDisposedException (ToString ());
            }
        }

        static string GeneratePrefix ()
        {
            foreach (var address in Dns.GetHostAddresses (Dns.GetHostName ())) {
                if (address.AddressFamily == AddressFamily.InterNetwork) {
                    return string.Format (
                        "http://{0}:{1}/f-spot/photo-sharing/", address, (new Random ()).Next (1024, 5000));
                }
            }
            
            return null;
        }
    }
}

