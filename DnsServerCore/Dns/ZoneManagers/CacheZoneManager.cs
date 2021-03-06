﻿/*
Technitium DNS Server
Copyright (C) 2021  Shreyas Zare (shreyas@technitium.com)

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

using DnsServerCore.Dns.ResourceRecords;
using DnsServerCore.Dns.Zones;
using System.Collections.Generic;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace DnsServerCore.Dns.ZoneManagers
{
    public sealed class CacheZoneManager : DnsCache
    {
        #region variables

        const uint FAILURE_RECORD_TTL = 30u;
        const uint NEGATIVE_RECORD_TTL = 300u;
        const uint MINIMUM_RECORD_TTL = 10u;
        const uint SERVE_STALE_TTL = 3 * 24 * 60 * 60; //3 days serve stale ttl as per https://www.rfc-editor.org/rfc/rfc8767.html suggestion

        readonly DnsServer _dnsServer;

        readonly ZoneTree<CacheZone> _root = new ZoneTree<CacheZone>();

        #endregion

        #region constructor

        public CacheZoneManager(DnsServer dnsServer)
            : base(FAILURE_RECORD_TTL, NEGATIVE_RECORD_TTL, MINIMUM_RECORD_TTL, SERVE_STALE_TTL)
        {
            _dnsServer = dnsServer;
        }

        #endregion

        #region protected

        protected override void CacheRecords(IReadOnlyList<DnsResourceRecord> resourceRecords)
        {
            //read and set glue records from base class
            foreach (DnsResourceRecord resourceRecord in resourceRecords)
            {
                IReadOnlyList<DnsResourceRecord> glueRecords = GetGlueRecordsFrom(resourceRecord);
                if (glueRecords.Count > 0)
                    resourceRecord.SetGlueRecords(glueRecords);
            }

            if (resourceRecords.Count == 1)
            {
                DnsResourceRecord resourceRecord = resourceRecords[0];

                if (resourceRecord.Name.Contains('*'))
                    return;

                CacheZone zone = _root.GetOrAdd(resourceRecord.Name, delegate (string key)
                {
                    return new CacheZone(resourceRecord.Name, 1);
                });

                zone.SetRecords(resourceRecord.Type, resourceRecords, _dnsServer.ServeStale);
            }
            else
            {
                Dictionary<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> groupedByDomainRecords = DnsResourceRecord.GroupRecords(resourceRecords);
                bool serveStale = _dnsServer.ServeStale;

                //add grouped records
                foreach (KeyValuePair<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> groupedByTypeRecords in groupedByDomainRecords)
                {
                    if (groupedByTypeRecords.Key.Contains('*'))
                        continue;

                    CacheZone zone = _root.GetOrAdd(groupedByTypeRecords.Key, delegate (string key)
                    {
                        return new CacheZone(groupedByTypeRecords.Key, groupedByTypeRecords.Value.Count);
                    });

                    foreach (KeyValuePair<DnsResourceRecordType, List<DnsResourceRecord>> groupedRecords in groupedByTypeRecords.Value)
                        zone.SetRecords(groupedRecords.Key, groupedRecords.Value, serveStale);
                }
            }
        }

        #endregion

        #region private

        private void ResolveCNAME(DnsQuestionRecord question, DnsResourceRecord lastCNAME, bool serveStale, List<DnsResourceRecord> answerRecords)
        {
            int queryCount = 0;

            do
            {
                if (!_root.TryGet((lastCNAME.RDATA as DnsCNAMERecord).Domain, out CacheZone cacheZone))
                    break;

                IReadOnlyList<DnsResourceRecord> records = cacheZone.QueryRecords(question.Type, serveStale, true);
                if (records.Count < 1)
                    break;

                answerRecords.AddRange(records);

                if (records[0].Type != DnsResourceRecordType.CNAME)
                    break;

                lastCNAME = records[0];
            }
            while (++queryCount < DnsServer.MAX_CNAME_HOPS);
        }

        private IReadOnlyList<DnsResourceRecord> GetAdditionalRecords(IReadOnlyList<DnsResourceRecord> refRecords, bool serveStale)
        {
            List<DnsResourceRecord> additionalRecords = new List<DnsResourceRecord>();

            foreach (DnsResourceRecord refRecord in refRecords)
            {
                switch (refRecord.Type)
                {
                    case DnsResourceRecordType.NS:
                        DnsNSRecord nsRecord = refRecord.RDATA as DnsNSRecord;
                        if (nsRecord is not null)
                            ResolveAdditionalRecords(refRecord, nsRecord.NameServer, serveStale, additionalRecords);

                        break;

                    case DnsResourceRecordType.MX:
                        DnsMXRecord mxRecord = refRecord.RDATA as DnsMXRecord;
                        if (mxRecord is not null)
                            ResolveAdditionalRecords(refRecord, mxRecord.Exchange, serveStale, additionalRecords);

                        break;

                    case DnsResourceRecordType.SRV:
                        DnsSRVRecord srvRecord = refRecord.RDATA as DnsSRVRecord;
                        if (srvRecord is not null)
                            ResolveAdditionalRecords(refRecord, srvRecord.Target, serveStale, additionalRecords);

                        break;
                }
            }

            return additionalRecords;
        }

        private void ResolveAdditionalRecords(DnsResourceRecord refRecord, string domain, bool serveStale, List<DnsResourceRecord> additionalRecords)
        {
            IReadOnlyList<DnsResourceRecord> glueRecords = refRecord.GetGlueRecords();
            if (glueRecords.Count > 0)
            {
                bool added = false;

                foreach (DnsResourceRecord glueRecord in glueRecords)
                {
                    if (!glueRecord.IsStale)
                    {
                        added = true;
                        additionalRecords.Add(glueRecord);
                    }
                }

                if (added)
                    return;
            }

            if (_root.TryGet(domain, out CacheZone cacheZone))
            {
                {
                    IReadOnlyList<DnsResourceRecord> records = cacheZone.QueryRecords(DnsResourceRecordType.A, serveStale, true);
                    if ((records.Count > 0) && (records[0].Type == DnsResourceRecordType.A))
                        additionalRecords.AddRange(records);
                }

                {
                    IReadOnlyList<DnsResourceRecord> records = cacheZone.QueryRecords(DnsResourceRecordType.AAAA, serveStale, true);
                    if ((records.Count > 0) && (records[0].Type == DnsResourceRecordType.AAAA))
                        additionalRecords.AddRange(records);
                }
            }
        }

        #endregion

        #region public

        public override void RemoveExpiredRecords()
        {
            bool serveStale = _dnsServer.ServeStale;

            foreach (CacheZone zone in _root)
            {
                zone.RemoveExpiredRecords(serveStale);

                if (zone.IsEmpty)
                    _root.TryRemove(zone.Name, out _); //remove empty zone
            }
        }

        public override void Flush()
        {
            _root.Clear();
        }

        public bool DeleteZone(string domain)
        {
            return _root.TryRemove(domain, out _);
        }

        public List<string> ListSubDomains(string domain)
        {
            return _root.ListSubDomains(domain);
        }

        public List<DnsResourceRecord> ListAllRecords(string domain)
        {
            if (_root.TryGet(domain, out CacheZone zone))
                return zone.ListAllRecords();

            return new List<DnsResourceRecord>(0);
        }

        public DnsDatagram QueryClosestDelegation(DnsDatagram request)
        {
            _ = _root.FindZone(request.Question[0].Name, out CacheZone delegation, out _, out _);
            if (delegation is not null)
            {
                //return closest name servers in delegation
                IReadOnlyList<DnsResourceRecord> closestAuthority = delegation.QueryRecords(DnsResourceRecordType.NS, false, true);
                if ((closestAuthority.Count > 0) && (closestAuthority[0].Type == DnsResourceRecordType.NS) && (closestAuthority[0].Name.Length > 0)) //dont trust root name servers from cache!
                {
                    IReadOnlyList<DnsResourceRecord> additional = GetAdditionalRecords(closestAuthority, false);

                    return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, false, false, DnsResponseCode.NoError, request.Question, null, closestAuthority, additional);
                }
            }

            //no cached delegation found
            return null;
        }

        public override DnsDatagram Query(DnsDatagram request, bool serveStaleAndResetExpiry = false)
        {
            DnsQuestionRecord question = request.Question[0];

            CacheZone zone = _root.FindZone(question.Name, out CacheZone delegation, out _, out _);
            if (zone is null)
            {
                //zone not found
                if (serveStaleAndResetExpiry)
                    return null; //recursive resolver does not make stale request so no need to return delegation response

                if (delegation is not null)
                {
                    //return closest name servers in delegation
                    IReadOnlyList<DnsResourceRecord> closestAuthority = delegation.QueryRecords(DnsResourceRecordType.NS, serveStaleAndResetExpiry, true);
                    if ((closestAuthority.Count > 0) && (closestAuthority[0].Type == DnsResourceRecordType.NS) && (closestAuthority[0].Name.Length > 0)) //dont trust root name servers from cache!
                    {
                        IReadOnlyList<DnsResourceRecord> additional = GetAdditionalRecords(closestAuthority, serveStaleAndResetExpiry);

                        return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, false, false, DnsResponseCode.NoError, request.Question, null, closestAuthority, additional);
                    }
                }

                //no cached delegation found
                return null;
            }

            //zone found
            IReadOnlyList<DnsResourceRecord> answers = zone.QueryRecords(question.Type, serveStaleAndResetExpiry, false);
            if (answers.Count > 0)
            {
                //answer found in cache
                DnsResourceRecord firstRR = answers[0];

                if (firstRR.RDATA is DnsEmptyRecord dnsEmptyRecord)
                {
                    if (serveStaleAndResetExpiry)
                    {
                        if (firstRR.IsStale)
                            firstRR.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04

                        if (dnsEmptyRecord.Authority is not null)
                        {
                            foreach (DnsResourceRecord record in dnsEmptyRecord.Authority)
                            {
                                if (record.IsStale)
                                    record.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04
                            }
                        }
                    }

                    return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, false, false, DnsResponseCode.NoError, request.Question, null, dnsEmptyRecord.Authority);
                }

                if (firstRR.RDATA is DnsNXRecord dnsNXRecord)
                {
                    if (serveStaleAndResetExpiry)
                    {
                        if (firstRR.IsStale)
                            firstRR.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04

                        if (dnsNXRecord.Authority is not null)
                        {
                            foreach (DnsResourceRecord record in dnsNXRecord.Authority)
                            {
                                if (record.IsStale)
                                    record.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04
                            }
                        }
                    }

                    return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, false, false, DnsResponseCode.NxDomain, request.Question, null, dnsNXRecord.Authority);
                }

                if (firstRR.RDATA is DnsFailureRecord dnsFailureRecord)
                {
                    if (serveStaleAndResetExpiry)
                    {
                        if (firstRR.IsStale)
                            firstRR.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04
                    }

                    return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, false, false, dnsFailureRecord.RCODE, request.Question);
                }

                DnsResourceRecord lastRR = answers[answers.Count - 1];
                if ((lastRR.Type != question.Type) && (lastRR.Type == DnsResourceRecordType.CNAME) && (question.Type != DnsResourceRecordType.ANY))
                {
                    List<DnsResourceRecord> newAnswers = new List<DnsResourceRecord>(answers);

                    ResolveCNAME(question, lastRR, serveStaleAndResetExpiry, newAnswers);

                    answers = newAnswers;
                }

                IReadOnlyList<DnsResourceRecord> additional = null;

                switch (question.Type)
                {
                    case DnsResourceRecordType.NS:
                    case DnsResourceRecordType.MX:
                    case DnsResourceRecordType.SRV:
                        additional = GetAdditionalRecords(answers, serveStaleAndResetExpiry);
                        break;
                }

                if (serveStaleAndResetExpiry)
                {
                    foreach (DnsResourceRecord record in answers)
                    {
                        if (record.IsStale)
                            record.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04
                    }

                    if (additional is not null)
                    {
                        foreach (DnsResourceRecord record in additional)
                        {
                            if (record.IsStale)
                                record.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04
                        }
                    }
                }

                return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, false, false, DnsResponseCode.NoError, request.Question, answers, null, additional);
            }
            else
            {
                //no answer in cache
                if (serveStaleAndResetExpiry)
                    return null; //recursive resolver does not make stale request so no need to return delegation response

                //check for closest delegation if any
                if (delegation is not null)
                {
                    //return closest name servers in delegation
                    IReadOnlyList<DnsResourceRecord> closestAuthority = delegation.QueryRecords(DnsResourceRecordType.NS, false, true);
                    if ((closestAuthority.Count > 0) && (closestAuthority[0].Type == DnsResourceRecordType.NS) && (closestAuthority[0].Name.Length > 0)) //dont trust root name servers from cache!
                    {
                        IReadOnlyList<DnsResourceRecord> additional = GetAdditionalRecords(closestAuthority, false);

                        return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, false, false, DnsResponseCode.NoError, request.Question, null, closestAuthority, additional);
                    }
                }

                //no cached delegation found
                return null;
            }
        }

        #endregion
    }
}
