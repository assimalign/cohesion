/*
Technitium Library
Copyright (C) 2025  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System.Threading.Tasks;
using Assimalign.Cohesion.Dns.ResourceRecords;

namespace Assimalign.Cohesion.Dns
{
    class ResolverNsRevalidationDnsCache : IDnsCache
    {
        #region variables

        readonly IDnsCache _cache;
        readonly DnsPacketQuestion _revalidationQuestion;

        #endregion

        #region constructor

        public ResolverNsRevalidationDnsCache(IDnsCache cache, DnsPacketQuestion revalidationQuestion)
        {
            _cache = cache;
            _revalidationQuestion = revalidationQuestion;
        }

        #endregion

        #region public

        public Task<DnsPacket> QueryClosestDelegationAsync(DnsPacket request)
        {
            return _cache.QueryClosestDelegationAsync(request);
        }

        public Task<DnsPacket> QueryAsync(DnsPacket request, bool serveStale = false, bool findClosestNameServers = false, bool resetExpiry = false)
        {
            if (_revalidationQuestion.Equals(request.Question[0]))
            {
                string parentZone = DnsCache.GetParentZone(_revalidationQuestion.Name);
                if (parentZone is null)
                    return Task.FromResult<DnsPacket>(null); //parent zone is root

                //return the closest name servers for parent zone
                return _cache.QueryClosestDelegationAsync(new DnsPacket(request.Id, false, DnsOpCode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError, new DnsPacketQuestion[] { new DnsPacketQuestion(parentZone, DnsRecordType.A, DnsClass.Internet) }));
            }

            return _cache.QueryAsync(request, serveStale, findClosestNameServers, resetExpiry);
        }

        public void CacheResponse(DnsPacket response, bool isDnssecBadCache = false, string zoneCut = null)
        {
            _cache.CacheResponse(response, isDnssecBadCache, zoneCut);
        }

        #endregion
    }
}
