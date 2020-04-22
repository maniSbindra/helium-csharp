using Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Helium.DataAccessLayer
{
    /// <summary>
    /// Data Access Layer for CosmosDB
    /// </summary>
    public partial class DAL : IDAL
    {
        public int DefaultPageSize { get; set; } = 100;
        public int MaxPageSize { get; set; } = 1000;
        public int CosmosTimeout { get; set; } = 60;
        public int CosmosMaxRetries { get; set; } = 10;

        private CosmosConfig cosmosDetails = null;

        /// <summary>
        /// Data Access Layer Constructor
        /// </summary>
        /// <param name="cosmosUrl">CosmosDB Url</param>
        /// <param name="cosmosKey">CosmosDB connection key</param>
        /// <param name="cosmosDatabase">CosmosDB Database</param>
        /// <param name="cosmosCollection">CosmosDB Collection</param>
        public DAL(Uri cosmosUrl, string cosmosKey, string cosmosDatabase, string cosmosCollection)
        {
            if (cosmosUrl == null)
            {
                throw new ArgumentNullException(nameof(cosmosUrl));
            }

            cosmosDetails = new CosmosConfig
            {
                MaxRows = MaxPageSize,


                Timeout = CosmosTimeout,
                CosmosCollection = cosmosCollection,
                CosmosDatabase = cosmosDatabase,
                CosmosKey = cosmosKey,
                CosmosUrl = cosmosUrl.AbsoluteUri
            };

            // create the CosmosDB client and container
            cosmosDetails.Client = OpenAndTestCosmosClient(cosmosUrl, cosmosKey, cosmosDatabase, cosmosCollection).GetAwaiter().GetResult();
            cosmosDetails.Container = cosmosDetails.Client.GetContainer(cosmosDatabase, cosmosCollection);
        }

        /// <summary>
        /// Recreate the Cosmos Client / Container (after a key rotation)
        /// </summary>
        /// <param name="cosmosUrl">Cosmos URL</param>
        /// <param name="cosmosKey">Cosmos Key</param>
        /// <param name="cosmosDatabase">Cosmos Database</param>
        /// <param name="cosmosCollection">Cosmos Collection</param>
        /// <param name="force">force reconnection even if no params changed</param>
        /// <returns>Task</returns>
        public async Task Reconnect(Uri cosmosUrl, string cosmosKey, string cosmosDatabase, string cosmosCollection, bool force = false)
        {
            if (cosmosUrl == null)
            {
                throw new ArgumentNullException(nameof(cosmosUrl));
            }

            if (force ||
                cosmosDetails.CosmosCollection != cosmosCollection ||
                cosmosDetails.CosmosDatabase != cosmosDatabase ||
                cosmosDetails.CosmosKey != cosmosKey ||
                cosmosDetails.CosmosUrl != cosmosUrl.AbsoluteUri)
            {
                CosmosConfig d = new CosmosConfig
                {
                    CosmosCollection = cosmosCollection,
                    CosmosDatabase = cosmosDatabase,
                    CosmosKey = cosmosKey,
                    CosmosUrl = cosmosUrl.AbsoluteUri
                };

                // open and test a new client / container
                d.Client = await OpenAndTestCosmosClient(cosmosUrl, cosmosKey, cosmosDatabase, cosmosCollection).ConfigureAwait(false);
                d.Container = d.Client.GetContainer(cosmosDatabase, cosmosCollection);

                // set the current CosmosDetail
                cosmosDetails = d;
            }
        }

        /// <summary>
        /// Open and test the Cosmos Client / Container / Query
        /// </summary>
        /// <param name="cosmosUrl">Cosmos URL</param>
        /// <param name="cosmosKey">Cosmos Key</param>
        /// <param name="cosmosDatabase">Cosmos Database</param>
        /// <param name="cosmosCollection">Cosmos Collection</param>
        /// <returns>An open and validated CosmosClient</returns>
        private async Task<CosmosClient> OpenAndTestCosmosClient(Uri cosmosUrl, string cosmosKey, string cosmosDatabase, string cosmosCollection)
        {
            // validate required parameters
            if (cosmosUrl == null)
            {
                throw new ArgumentNullException(nameof(cosmosUrl));
            }

            if (string.IsNullOrEmpty(cosmosKey))
            {
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, $"CosmosKey not set correctly {cosmosKey}"));
            }

            if (string.IsNullOrEmpty(cosmosDatabase))
            {
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, $"CosmosDatabase not set correctly {cosmosDatabase}"));
            }

            if (string.IsNullOrEmpty(cosmosCollection))
            {
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, $"CosmosCollection not set correctly {cosmosCollection}"));
            }

            // open and test a new client / container
            var c = new CosmosClient(cosmosUrl.AbsoluteUri, cosmosKey, cosmosDetails.CosmosClientOptions);
            var con = c.GetContainer(cosmosDatabase, cosmosCollection);
            await con.ReadItemAsync<dynamic>("action", new PartitionKey("0")).ConfigureAwait(false);

            return c;
        }

        /// <summary>
        /// Compute the partition key based on the movieId or actorId
        /// 
        /// For this sample, the partitionkey is the id mod 10
        /// 
        /// In a full implementation, you would update the logic to determine the partition key
        /// </summary>
        /// <param name="id">document id</param>
        /// <returns>the partition key</returns>
        public static string GetPartitionKey(string id)
        {
            // validate id
            if (!string.IsNullOrEmpty(id) &&
                id.Length > 5 &&
                (id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) || id.StartsWith("nm", StringComparison.OrdinalIgnoreCase)) &&
                int.TryParse(id.Substring(2), out int idInt))
            {
                return (idInt % 10).ToString(CultureInfo.InvariantCulture);
            }

            throw new ArgumentException("Invalid Partition Key");
        }

        /// <summary>
        /// Generic function to be used by subclasses to execute arbitrary queries and return type T.
        /// </summary>
        /// <typeparam name="T">POCO type to which results are serialized and returned.</typeparam>
        /// <param name="queryDefinition">Query to be executed.</param>
        /// <returns>Enumerable list of objects of type T.</returns>
        private async Task<IEnumerable<T>> InternalCosmosDBSqlQuery<T>(QueryDefinition queryDefinition)
        {
            // run query
            var query = cosmosDetails.Container.GetItemQueryIterator<T>(queryDefinition, requestOptions: cosmosDetails.QueryRequestOptions);

            List<T> results = new List<T>();

            var pages = query.AsPages(null, 1000);

            await foreach (var p in pages)
            {
                results.AddRange(p.Values);
            }

            return results;
        }

        /// <summary>
        /// Generic function to be used by subclasses to execute arbitrary queries and return type T.
        /// </summary>
        /// <typeparam name="T">POCO type to which results are serialized and returned.</typeparam>
        /// <param name="sql">Query to be executed.</param>
        /// <returns>Enumerable list of objects of type T.</returns>
        private async Task<IEnumerable<T>> InternalCosmosDBSqlQuery<T>(string sql)
        {
            // run query
            var query = cosmosDetails.Container.GetItemQueryStreamIterator(sql, requestOptions: cosmosDetails.QueryRequestOptions);
            List<T> results = new List<T>();

            JsonSerializerOptions options = new JsonSerializerOptions
            {
                IgnoreNullValues = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
            };
            options.Converters.Add(new JsonStringEnumConverter());
            options.Converters.Add(new TimeSpanConverter());


            //var pages = query.AsPages(null, 1000);

            await foreach (var p in query)
            {
                var stream = p.ContentStream;
                byte[] buff = new byte[stream.Length];
                stream.Read(buff, 0, (int)stream.Length);
                string s = System.Text.Encoding.UTF8.GetString(buff);

#pragma warning disable CA1307 // Specify StringComparison
                s = s.Substring(0, s.IndexOf("\"_count\":") - 1);
                s = s.Substring(s.IndexOf("\"Documents\":") + 12);
#pragma warning restore CA1307 // Specify StringComparison

                var res = JsonSerializer.Deserialize<List<T>>(s, options);

                results.AddRange(res);
            }

            return results;
        }
    }

    public class TempResult<T>
    {
#pragma warning disable CA1707 // Identifiers should not contain underscores
        public string _rid { get; set; }
#pragma warning disable CA2227 // Collection properties should be read only
        public List<T> Documents { get; set; }
#pragma warning restore CA2227 // Collection properties should be read only
        public int _count { get; set; }
#pragma warning restore CA1707 // Identifiers should not contain underscores
    }
}