﻿using NTMiner.Core.MinerServer;
using System;
using System.Collections.Generic;

namespace NTMiner.Controllers {
    public interface IFileUrlController {
        string NTMinerUrl(NTMinerUrlRequest request);
        List<NTMinerFileData> NTMinerFiles();
        /// <summary>
        /// 需签名
        /// </summary>
        ResponseBase AddOrUpdateNTMinerFile(DataRequest<NTMinerFileData> request);
        /// <summary>
        /// 需签名
        /// </summary>
        ResponseBase RemoveNTMinerFile(DataRequest<Guid> request);
        string NTMinerUpdaterUrl();
        string MinerClientFinderUrl();
        string LiteDbExplorerUrl();
        string PackageUrl(PackageUrlRequest request);
    }
}
