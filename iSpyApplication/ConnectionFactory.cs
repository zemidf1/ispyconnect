using System;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace iSpyApplication
{
    public static class ConnectionFactory
    {

        private static string CalculateMd5Hash(
            string input)
        {
            var inputBytes = Encoding.ASCII.GetBytes(input);
            var hash = MD5.Create().ComputeHash(inputBytes);
            var sb = new StringBuilder();
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string GrabHeaderVar(
            string varName,
            string header)
        {
            var regHeader = new Regex(string.Format(@"{0}=""([^""]*)""", varName));
            var matchHeader = regHeader.Match(header);
            if (matchHeader.Success)
                return matchHeader.Groups[1].Value;
            throw new ApplicationException(string.Format("Header {0} not found", varName));
        }

        private static string GetDigestHeader(string url, string username, string password, string realm, string nonce, int nc, string cnonce, string qop)
        {
            nc = nc + 1;
            var uri = new Uri(url);
            var dir = uri.PathAndQuery;
            var ha1 = CalculateMd5Hash(string.Format("{0}:{1}:{2}", username, realm, password));
            var ha2 = CalculateMd5Hash(string.Format("{0}:{1}", "GET", dir));
            var digestResponse =
                CalculateMd5Hash(string.Format("{0}:{1}:{2:00000000}:{3}:{4}:{5}", ha1, nonce, nc, cnonce, qop,
                    ha2));

            return string.Format("Digest username=\"{0}\", realm=\"{1}\", nonce=\"{2}\", uri=\"{3}\", " +
                                    "algorithm=MD5, response=\"{4}\", qop={5}, nc={6:00000000}, cnonce=\"{7}\"",
                username, realm, nonce, dir, digestResponse, qop, nc, cnonce);


        }

        public static HttpWebResponse GetResponse(string source, out HttpWebRequest request)
        {
            var uri = new Uri(source);
            string username = "", password = "";
            if (!String.IsNullOrEmpty(uri.UserInfo))
            {
                var lp = uri.UserInfo.Split(':');
                if (lp.Length > 1)
                {
                    username = lp[0];
                    password = lp[1];
                }
            }
            return GetResponse(source, "", "", "", null, false, true, 5000, username, password, out request);
        }

        public static HttpWebResponse GetResponse(string source, string cookies, string username, string password, out HttpWebRequest request)
        {
            return GetResponse(source, cookies, "", "", null, false, true, 5000, username, password, out request);
        }

        public static HttpWebResponse GetResponse(string source, string cookies, string headers, string userAgent, IWebProxy proxy, bool useHttp10, bool useSeparateConnectionGroup, int requestTimeout, string username, string password, out HttpWebRequest request)
        {
            request = GenerateRequest(source, cookies, headers, userAgent, proxy, useHttp10, useSeparateConnectionGroup, requestTimeout, username, password);

            HttpWebResponse response = null;
            try
            {
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException ex)
            {
                // Try to fix a 401 exception by adding a Authorization header
                if (ex.Response == null || ((HttpWebResponse)ex.Response).StatusCode != HttpStatusCode.Unauthorized)
                    return null;

                var wwwAuthenticateHeader = ex.Response.Headers["WWW-Authenticate"];
                var realm = GrabHeaderVar("realm", wwwAuthenticateHeader);
                var nonce = GrabHeaderVar("nonce", wwwAuthenticateHeader);
                var qop = GrabHeaderVar("qop", wwwAuthenticateHeader);

                if (response != null)
                    response.Close();

                int nc = 0;
                string cnonce = new Random().Next(123400, 9999999).ToString(CultureInfo.InvariantCulture);

                request = GenerateRequest(source, cookies, headers, userAgent, proxy, useHttp10, useSeparateConnectionGroup, requestTimeout, username, password);
                request.Headers["Authorization"] = GetDigestHeader(source, username, password, realm, nonce, nc, cnonce, qop);
                try
                {
                    response = (HttpWebResponse)request.GetResponse();
                }
                catch (Exception ex2)
                {
                    response = null;
                }
            }
            catch (Exception ex2)
            {
                response = null;
            }

            return response;
        }

        private static HttpWebRequest GenerateRequest(string source, string cookies, string headers, string userAgent, IWebProxy proxy, bool useHttp10, bool useSeparateConnectionGroup, int requestTimeout, string username, string password)
        {
            var request = (HttpWebRequest)WebRequest.Create(source);
            // set user agent
            if (!String.IsNullOrEmpty(userAgent))
            {
                request.UserAgent = userAgent;
            }

            // set proxy
            if (proxy != null)
            {
                request.Proxy = proxy;
            }

            if (useHttp10)
                request.ProtocolVersion = HttpVersion.Version10;

            // set timeout value for the request
            request.Timeout = request.ServicePoint.ConnectionLeaseTimeout = request.ServicePoint.MaxIdleTime = requestTimeout;
            request.AllowAutoRedirect = true;

            // set login and password
            if (!String.IsNullOrEmpty(username))
                request.Credentials = new NetworkCredential(username, password);
            // set connection group name
            if (useSeparateConnectionGroup)
                request.ConnectionGroupName = request.GetHashCode().ToString(CultureInfo.InvariantCulture);
            // force basic authentication through extra headers if required

            var authInfo = "";
            if (!String.IsNullOrEmpty(username))
            {
                authInfo = Convert.ToBase64String(Encoding.Default.GetBytes(username + ":" + password));
                request.Headers["Authorization"] = "Basic " + authInfo;
            }


            if (!String.IsNullOrEmpty(cookies))
            {
                cookies = cookies.Replace("[AUTH]", authInfo);
                var myContainer = new CookieContainer();
                string[] coll = cookies.Split(';');
                foreach (var ckie in coll)
                {
                    if (!String.IsNullOrEmpty(ckie))
                    {
                        string[] nv = ckie.Split('=');
                        if (nv.Length == 2)
                        {
                            var cookie = new Cookie(nv[0].Trim(), nv[1].Trim());
                            myContainer.Add(new Uri(request.RequestUri.ToString()), cookie);
                        }
                    }
                }
                request.CookieContainer = myContainer;
            }

            if (!String.IsNullOrEmpty(headers))
            {
                headers = headers.Replace("[AUTH]", authInfo);
                string[] coll = headers.Split(';');
                foreach (var hdr in coll)
                {
                    if (!String.IsNullOrEmpty(hdr))
                    {
                        string[] nv = hdr.Split('=');
                        if (nv.Length == 2)
                        {
                            request.Headers.Add(nv[0], nv[1]);
                        }
                    }
                }
            }

            return request;
        }
    }
}