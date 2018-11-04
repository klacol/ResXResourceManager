namespace tomenglertde.ResXManager.Translators
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel.Composition;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Diagnostics.Contracts;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Runtime.Serialization;
    using System.Text;
    using System.Threading.Tasks;

    using JetBrains.Annotations;

    using Newtonsoft.Json;

    using tomenglertde.ResXManager.Infrastructure;

    using TomsToolbox.Core;
    using TomsToolbox.Desktop;

    [Export(typeof(ITranslator))]
    public class DeeplTranslator : TranslatorBase
    {
        [NotNull]
        private static readonly string _apiBaseUri = "https://api.deepl.com/v2/translate";
        [NotNull]
        private static readonly Uri _webUri = new Uri("https://www.deepl.com/api.html");
        [NotNull, ItemNotNull]
        private static readonly IList<ICredentialItem> _credentialItems = new ICredentialItem[] { new CredentialItem("APIKey", "APIKey") };

        public DeeplTranslator()
            : base("Deepl", "Deepl", _webUri, _credentialItems)
        {
        }

        private string APIKey => Credentials[0].Value;

        public override void Translate(ITranslationSession translationSession)
        {
            if (string.IsNullOrEmpty(APIKey))
            {
                translationSession.AddMessage("The API of Deepl requires an Key.");
                return;
            }

            foreach (var languageGroup in translationSession.Items.GroupBy(item => item.TargetCulture))
            {
                if (translationSession.IsCanceled)
                    break;

                Contract.Assume(languageGroup != null);

                var targetCulture = languageGroup.Key.Culture ?? translationSession.NeutralResourcesLanguage;

                using (var itemsEnumerator = languageGroup.GetEnumerator())
                {
                    var loop = true;
                    while (loop)
                    {
                        var sourceItems = itemsEnumerator.Take(50);
                        if (translationSession.IsCanceled || !sourceItems.Any())
                            break;

                        // Build out list of parameters
                        var parameters = new List<string>(30);
                        foreach (var item in sourceItems)
                        {
                            parameters.AddRange(new[] { "text", RemoveKeyboardShortcutIndicators(item.Source) });
                        }

                        parameters.AddRange(new[] {
                            "target_lang", DeeplLangCode(targetCulture),
                            "source_lang", DeeplLangCode(translationSession.SourceLanguage),
                            "auth_key", APIKey });

                        // Call the Deepl API
                        
                        //var responseTask = GetHttpResponse(apiBaseUrl, null, parameters, JsonConverter<TranslationRootObject>);
                        string answer = string.Empty;
                        using (var wc = new WebClient())
                        {
                            string requestUrl = BuildUrl(_apiBaseUri, parameters);
                            WebRequest request = WebRequest.Create(requestUrl);
                            request.Method = "POST";
                            request.ContentType = "application/x-www-form-urlencoded";
                            //request.ContentLength = SourceTextUrlEncoded.Length;
                            HttpWebResponse response = null;

                            bool repeat = false;
                            do
                            {
                                try
                                {
                                    response = (HttpWebResponse)request.GetResponse();
                                }

                                catch (Exception ex)
                                {
                                    Console.WriteLine(ex.Message);

                                    if (ex.Message == "The Remoteserver has return an Error: (456) Quota Exceeded.")
                                    {
                                        translationSession.AddMessage("The quota at Deepl is exceeded, The translator will be aborted.");
                                        translationSession.AddMessage("Check you DeepL account : https://www.deepl.com/consumption.html.");
                                    }
                                    repeat = false;
                                }

                                if (response == null)
                                {
                                    repeat = false;
                                    continue;
                                }

                                if (response.StatusCode.ToString() == "414")
                                {
                                    repeat = true;
                                    System.Threading.Thread.Sleep(5 * 1000);
                                    continue;
                                }

                                if (response.StatusCode.ToString() == "429")
                                {
                                    repeat = true;
                                    System.Threading.Thread.Sleep(5 * 1000);
                                    continue;
                                }

                                if (response.StatusCode.ToString() == "456")
                                {
                                    translationSession.AddMessage("The Quota at Deepl is exceeded, The translator will be aborted.");
                                    translationSession.AddMessage("(456) Quota Exceeded");
                                }

                            } while (repeat);
                            
                            if (response != null)
                                using (var sr = new StreamReader(response.GetResponseStream()))
                                {
                                    answer = sr.ReadToEnd();
                                }
                        }

                        DeeplTranslationResult deeplTranslationResult = JsonConvert.DeserializeObject<DeeplTranslationResult>(answer);

                        foreach (var item in translationSession.Items)
                        {
                            if (translationSession.IsCanceled)
                                break;

                            Contract.Assume(item != null);

                            translationSession.Dispatcher.BeginInvoke(() =>
                            {
                                Contract.Requires(item != null);
                                Contract.Requires(deeplTranslationResult.translations != null);

                                foreach (var translation in deeplTranslationResult.translations)
                                {
                                    Contract.Assume(translation != null);
                                    item.Results.Add(new TranslationMatch(this, translation.text, 1));
                                }
                            });

                        }
                    }
                }
            }
        }

        [NotNull]
        private static string DeeplLangCode([NotNull] CultureInfo cultureInfo)
        {
            //Deepl support this languages: EN,DE,FR,ES,IT,NL,PL
            //Deepl expects the only the language code, not the culture
            string[] supportedLangaugaes = new[] { "EN", "DE", "FR", "ES", "IT", "NL", "PL" };

            var iso1 = cultureInfo.TwoLetterISOLanguageName;

            if (!supportedLangaugaes.Contains(iso1.ToUpper()))
            {
                return $"The language {iso1} is not supported by Deepl";
            }
  
            return iso1.ToUpper();
        }

        private static async Task<T> GetHttpResponse<T>(string baseUrl, string authHeader, [NotNull] ICollection<string> parameters, Func<Stream, T> conv)
        {
            var url = BuildUrl(baseUrl, parameters);
            using (var c = new HttpClient())
            {
                if (!string.IsNullOrWhiteSpace(authHeader))
                {
                    c.DefaultRequestHeaders.Add("Authorization", authHeader);
                }

                Debug.WriteLine("Deepl URL: " + url);
                using (var stream = await c.GetStreamAsync(url))
                {
                    return conv(stream);
                }
            }
        }

        private static T JsonConverter<T>([NotNull] Stream stream)
        {
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return JsonConvert.DeserializeObject<T>(reader.ReadToEnd());
            }
        }

        [SuppressMessage("Microsoft.Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated by the data contract serializer")]
        [DataContract]
        private class Translation
        {
            [DataMember(Name = "translatedText")]
            public string TranslatedText { get; set; }
        }

        [SuppressMessage("Microsoft.Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated by the data contract serializer")]
        [DataContract]
        private class Data
        {
            [DataMember(Name = "translations")]
            public List<Translation> Translations { get; set; }
        }

        [SuppressMessage("Microsoft.Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated by the data contract serializer")]
        [DataContract]
        private class TranslationRootObject
        {
            [DataMember(Name="data")]
            public Data Data { get; set; }
        }

        /// <summary>Builds the URL from a base, method name, and name/value paired parameters. All parameters are encoded.</summary>
        /// <param name="url">The base URL.</param>
        /// <param name="pairs">The name/value paired parameters.</param>
        /// <returns>Resulting URL.</returns>
        /// <exception cref="System.ArgumentException">There must be an even number of strings supplied for parameters.</exception>
        [NotNull]
        private static string BuildUrl(string url, [NotNull, ItemNotNull] ICollection<string> pairs)
        {
            if (pairs.Count % 2 != 0)
                throw new ArgumentException("There must be an even number of strings supplied for parameters.");

            var sb = new StringBuilder(url);
            if (pairs.Count > 0)
            {
                sb.Append("?");
                sb.Append(string.Join("&", pairs.Where((s, i) => i % 2 == 0).Zip(pairs.Where((s, i) => i % 2 == 1), Enc)));
            }
            return sb.ToString();

            string Enc(string a, string b) => string.Concat(System.Web.HttpUtility.UrlEncode(a), "=", System.Web.HttpUtility.UrlEncode(b));
        }

        private class DeeplTranslation
        {
            public string detected_source_language { get; set; }
            public string text { get; set; }
        }

        private class DeeplTranslationResult
        {
            public List<DeeplTranslation> translations { get; set; }
        }
    }
}