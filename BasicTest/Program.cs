using StackExchange.Redis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System;
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Threading.Tasks;

[assembly: AssemblyVersion("1.0.0")]

namespace BasicTest
{
    static class Program
    {
        static async Task Main()
        {
            using (var conn = ConnectionMultiplexer.Connect("127.0.0.1:6379"))
            {
                var db = conn.GetDatabase();

                RedisKey key = Me();
                db.KeyDelete(key);
                db.StringSet(key, "abc");

                string s = (string)db.ScriptEvaluate(@"
    local val = redis.call('get', KEYS[1])
    redis.call('del', KEYS[1])
    return val", new RedisKey[] { key }, flags: CommandFlags.NoScriptCache);

                db.StringSet("k1", "v1");
                db.StringSet("k2", "v2");
                db.StringSet("k3", "v3");
                db.StringSet("k4", "v4");

                

                for (int i = 0; ; i++)
                {
                    var values = await db.PipelineStringGetAsync(new RedisKey[] {"k1", "k2", "k3", "k4"});
                    Console.WriteLine(i + " " + String.Join(",", values));
                    if (values.Length != 4)
                    {
                        throw new Exception();
                    }    
                }
                

            }
        }

        internal static string Me([CallerMemberName] string caller = null)
        {
            return caller;
        }
    }
}
