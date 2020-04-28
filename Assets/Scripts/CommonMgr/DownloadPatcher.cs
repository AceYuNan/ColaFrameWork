using UnityEngine;
using System.Collections.Generic;
using System.IO;
using ColaFramework.Foundation;
using Plugins.XAsset;
using ColaFramework.Foundation.DownLoad;
using LitJson;

namespace ColaFramework
{
    /// <summary>
    /// 下载热更补丁的Patcher
    /// </summary>
    public class DownloadPatcher : UpdateTaskBase
    {
        #region Instance

        static DownloadPatcher _instance;
        public static DownloadPatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new DownloadPatcher();
                }
                return _instance;
            }
        }
        #endregion
        private enum CheckState
        {
            None,
            Checking,
            Done,
        }

        public DownloadPatcher()
        {
            m_StopWhenFail = true;  //下载失败之后不继续其他任务
        }

        private System.Action<bool> m_onDownPatchDone;      //参数bool： 是否需要解压文件
        private string m_strNewVersion = "";

        Dictionary<string, ABFileInfo> m_dicSvrVersions;
        Dictionary<string, ABFileInfo> m_dicLocalVersions;
        List<ABFileInfo> m_lstDiffVersions;
        float m_totalSize = 0;    // 总下载大小
        float m_totalUnpackSize = 0;  // 解压后所需大小
        float m_haveDownedSize = 0; // 已下载大小
        float m_lastDownedSize = 0;
        Object m_lockProgress;
        Object m_lockMsg;
        string m_strErrMsg = "";
        string m_strDownloadUrl;        //热更下载url
        string m_strVersionInfoUrl;     //下载版本信息URL
        float m_fStartDowndTime = 0;
        private float checkVersionTimeOut = 3f;

        public override void Reset()
        {
            m_haveDownedSize = 0;
            m_onDownPatchDone = null;
            m_strErrMsg = "";
            if (m_dicSvrVersions != null)
            {
                m_dicSvrVersions.Clear();
            }
            if (m_dicLocalVersions != null)
            {
                m_dicLocalVersions.Clear();
            }
            if (m_lstDiffVersions != null)
            {
                m_lstDiffVersions.Clear();
            }

            base.Reset();
        }

        // 检查是否有更新
        public void StartUpdate(System.Action<bool> callback)
        {
            CheckOverrideInstall();
            m_onDownPatchDone = callback;
            m_strVersionInfoUrl = AppConst.VersionHttpUrl;
            CheckNeedUpdate();
        }

        private void CheckOverrideInstall()
        {
            var buildVersion = CommonHelper.APKBuildVersion;
            var packageVersion = CommonHelper.PackageVersion;
            if(buildVersion != packageVersion)
            {
                OverrideInstallAPP(buildVersion);
            }
        }

        private void CheckNeedUpdate()
        {
            Debug.Log("开始版本检查");

            string strURL = string.Format(m_strVersionInfoUrl, Utility.GetPlatform(), "app_version.json", Utility.GetPlatform(), CommonHelper.PackageVersion);
            Debug.LogFormat("Request Version URL:{0}", strURL);
            m_fStartDowndTime = Time.time;
            HttpDownloadMgr.DownloadText(strURL, OnDownloadVersion, checkVersionTimeOut);
        }

        // 下载版本文件
        private void OnDownloadVersion(ErrorCode code, string msg, string strContent)
        {
            Debug.Log("下载版本信息文件");
            if (code != ErrorCode.SUCCESS)  //失败的情况
            {
                if (code == ErrorCode.TIME_OUT)
                {
                    // 超时切换地址重试重试
                    if (m_strVersionInfoUrl == AppConst.BakVersionHttpUrl)
                    {
                        m_strVersionInfoUrl = AppConst.VersionHttpUrl;
                    }
                    else
                    {
                        m_strVersionInfoUrl = AppConst.BakVersionHttpUrl;
                    }
                    Debug.LogFormat("超时，切换地址重试，url:{0}", m_strVersionInfoUrl);
                    CheckNeedUpdate();
                    return;
                }
                else
                {
                    Debug.LogWarningFormat("Download version info fail, error:{0}", msg);
                    if (m_strVersionInfoUrl != AppConst.BakVersionHttpUrl)
                    {
                        Debug.Log("使用备用地址获取版本更新信息");
                        m_strVersionInfoUrl = AppConst.BakVersionHttpUrl;
                        CheckNeedUpdate();
                        return;
                    }
                }

                //string strTips = "获取最新版本信息失败，请检查网络后重试。";
                //string strBtn = "重试";
                //EventMgr.onLaunchConfirmTips(strTips, strBtn, () =>
                //{
                //    CheckNeedUpdate();
                //}, true);
            }
            else // 成功的情况
            {
                Debug.LogFormat("OnDownloadVersion success ：{0}", strContent);

                AppVersion appVersion;
                try
                {
                    appVersion = JsonMapper.ToObject<AppVersion>(strContent);
                }
                catch (System.Exception ex)
                {
                    DoneWithNoDownload();
                    Debug.LogErrorFormat("解析VersionInfo的Json失败：{0}", ex.Message);
                    return;
                }
                if (null == appVersion)
                {
                    Debug.LogWarningFormat("Parse version info fail, content:{0}", strContent);
                    DoneWithNoDownload();
                    return;
                }

                m_strNewVersion = appVersion.Version;           // 热更版本号 x1.x2.x3.x4
                string minVersion = appVersion.MinVersion;      // 强更版本号 x1.x2.x3.x4 如果不需要强更返回空
                string RecommandVersion = appVersion.RecommandVersion;       // 最低弹窗版本 x1.x2.x3.x4
                string strUpdateNotice = appVersion.UpdateContent;    // 更新公告
                if (null != appVersion.Data)
                {
                    string url;
                    if (appVersion.Data.TryGetValue("url", out url))
                    {
                        AppConst.CDNUrl = url;
                    }
                }
                m_strDownloadUrl = AppConst.CDNUrl;

                Debug.LogFormat("GameNewVersionInfo,version:{0}, ForceVersion:{1}, TipVersion:{2}, CDN_Url:{3}, BakCDN_Url:{4}",
                    m_strNewVersion, minVersion, RecommandVersion, AppConst.CDNUrl, AppConst.BakCDNUrl);


                // 当前版本号如果小于强更版本号，走强更操作
                var CurrentVersion = CommonHelper.HotUpdateVersion;
                if (-1 == CommonHelper.CompareVersion(CurrentVersion, minVersion))
                {
                    NoticeForceUpdate(strUpdateNotice, false, CurrentVersion);
                    return;
                }
                if (-1 == CommonHelper.CompareVersion(CurrentVersion, RecommandVersion))
                {
                    NoticeForceUpdate(strUpdateNotice, true, CurrentVersion);
                    return;
                }

                //两个都没走，再去走热更逻辑
                DealWithHotFixStep(CurrentVersion);
            }
        }

        private void DealWithHotFixStep(string CurrentVersion)
        {
            // 有热更版本
            if (CurrentVersion != m_strNewVersion)
            {
                Debug.Log("版本不一致，需要热更");
                // 先当做热更处理
                ResetPatchCachePath(m_strNewVersion);

                //需要更新 开始校验MD5文件
                DowndLoadMd5File();
            }
            else
            {
                // 不需要更新
                DoneWithNoDownload();
            }
        }

        // 强更提醒 有配置就用配置，没配置就用默认提醒
        private void NoticeForceUpdate(string strUpdateNotice, bool canSkip, string CurrentVersion)
        {
            System.Action onSkip = null;
            if (canSkip)
            {
                onSkip = () =>
                {
                    DealWithHotFixStep(CurrentVersion);
                };
            }

            Debug.LogFormat("强更提醒,canSkip:{0}, notice:{1}", canSkip, strUpdateNotice);
            // string strTitle = "更新公告";
            // EventMgr.onUpdateNotice(strTitle, strUpdateNotice, ()=>{
            //     NoticeForceUpdate(strUpdateNotice, canSkip);
            //     // GameHelper.ToAppStore();
            // }, onSkip);
        }

        private void DoneWithNoDownload()
        {
            m_onDownPatchDone(false);
            // EventMgr.onProgressChange.Invoke(1);
        }

        // 重新设置一下缓存路径
        private void ResetPatchCachePath(string strNewVersion)
        {
            //updatecache目录的版本号如果和最新版本号不一致 要先清空缓存路径
            if (!CommonHelper.IsValueEqualPrefs(AppConst.KEY_CACHE_HOTFIX_VERSION, strNewVersion))
            {
                FileHelper.RmDir(AppConst.UpdateCachePath);
                FileHelper.Mkdir(AppConst.UpdateCachePath);
                PlayerPrefs.SetString(AppConst.KEY_CACHE_HOTFIX_VERSION, strNewVersion);
            }
        }

        /// <summary>
        /// 重装APP后的操作,需要覆盖安装
        /// 会清空各种沙盒缓存和注册的一些键值
        /// </summary>
        private void OverrideInstallAPP(string newVersion)
        {
            Debug.Log("** 覆盖安装App **");
            FileHelper.RmDir(AppConst.UpdateCachePath);
            FileHelper.RmDir(AppConst.CachePath);
            FileHelper.RmDir(AppConst.DataPath);
            FileHelper.RmDir(Utility.UpdatePath);
            CommonHelper.PackageVersion = newVersion;
        }

        // 下载md5 文件
        private void DowndLoadMd5File()
        {
            m_fStartDowndTime = Time.time;
            Debug.LogFormat("---DowndLoadMd5File,  url:{0}", m_strDownloadUrl);
            string strMd5URL = m_strDownloadUrl + AppConst.VersionFileName;
            HttpDownloadMgr.DownloadText(strMd5URL, OnDownloadMd5File);
        }

        // 下载md5文件
        public void OnDownloadMd5File(ErrorCode code, string msg, string strText)
        {
            //EventMgr.onProgressChange.Invoke(0.5f);
            if (code != ErrorCode.SUCCESS)  //失败的情况
            {
                // 首次失败 尝试使用备用地址下载
                if (m_strDownloadUrl != AppConst.BakCDNUrl)
                {
                    Debug.Log("使用备用地址进行热更。");
                    m_strDownloadUrl = AppConst.BakCDNUrl;
                    DowndLoadMd5File();
                    return;
                }

                // 还是失败，弹提示，玩家自己控制重试，这个时候会切回使用主下载地址
                Debug.LogErrorFormat("Download version file fail, error:{0}", msg);
                string strTips = "下载更新文件失败，请重试";
                string strBtn = "重试";
                //EventMgr.onLaunchConfirmTips(strTips, strBtn, () =>
                //{
                //    m_strDownloadUrl = m_strMainDownloadUrl;
                //    DowndLoadMd5File();
                //}, true);
            }
            else    // 成功的情况
            {
                Debug.Log("下载MD5文件成功。");
                m_dicSvrVersions = FileHelper.ReadABVersionFromText(strText);

                //在这一步可以排除掉不需要更新的文件，比如32bit和64bit的LuaJit文件

                CalDiffToDownload();
            }
        }

        // 计算差异文件并开始下载
        private void CalDiffToDownload()
        {
            m_lstDiffVersions = m_lstDiffVersions == null ? new List<ABFileInfo>() : m_lstDiffVersions;
            m_lstDiffVersions.Clear();
            m_totalSize = 0;

            //先检索沙盒目录的verions文件，如果没有，证明是新包会去Resource下读取versions文件
            string localVersionFilePath = AppConst.DataPath + "/" + AppConst.VersionFileName;
            if (File.Exists(localVersionFilePath))
            {
                m_dicLocalVersions = FileHelper.ReadABVersionInfo(localVersionFilePath);
            }
            else
            {
                localVersionFilePath = AppConst.VersionFileName;
                var textAsset = Resources.Load<TextAsset>(localVersionFilePath);
                m_dicLocalVersions = FileHelper.ReadABVersionFromText(textAsset.text);
                Resources.UnloadAsset(textAsset);
            }
            Debug.LogFormat("本地Md5文件路径：{0}", localVersionFilePath);

            foreach (KeyValuePair<string, ABFileInfo> pair in m_dicSvrVersions)
            {
                ABFileInfo svrInfo = pair.Value;
                ABFileInfo localInfo = null;
                m_dicLocalVersions.TryGetValue(svrInfo.filename, out localInfo);
                if (localInfo == null || localInfo.md5 != svrInfo.md5)
                {
                    // 如果有差异，先删除本地文件
                    if (localInfo != null)
                    {
                        string localFilePath = Utility.UpdatePath + localInfo.filename;
                        FileHelper.DeleteFile(localFilePath);
                    }
                    //排除和母包中md5一样的热更列表
                    bool isNeedDownLoad = localInfo.md5 != svrInfo.md5;
                    if (isNeedDownLoad)
                    {
                        string strCachePath = AppConst.UpdateCachePath + "/" + svrInfo.filename;
                        if (!FileHelper.IsFileExist(strCachePath))
                        {
                            m_totalSize += svrInfo.compressSize;
                            m_lstDiffVersions.Add(svrInfo);
                        }
                    }
                }
            }


            Debug.LogFormat("需要下载的更新大小：{0}, 差异文件数量：{1}", m_totalSize, m_lstDiffVersions.Count);
            if (m_totalSize > 0)
            {
                //wifi或者数据下载量小于设定值，直接下载
                if (Common_Utils.IsWifi || m_totalSize < AppConst.AUTO_DOWNLOAD_SIZE)
                {
                    RealDownloadPatch();
                }
                else
                {
                    string strTips = string.Format("是否确定使用手机流量下载[{0}]的游戏资源？", CommonHelper.FormatKB(m_totalSize));
                    string strBtn = "继续";
                    //EventMgr.onLaunchConfirmTips(strTips, strBtn, RealDownloadPatch, false);
                }
            }
            else
            {
                m_onDownPatchDone(true);
            }
        }

        //用户点击了确定按钮
        private void RealDownloadPatch()
        {
            float needSize = (m_totalUnpackSize + m_totalSize) * 1.2f;
            // TODO:正确的磁盘容量自己写接口取获取
            long diskSize = 1073741824;
            Debug.LogFormat("下载补丁，磁盘容量：{0}, 需要容量：{1}", diskSize, needSize);
            if (diskSize < needSize)
            {
                float diff = needSize - diskSize;
                string strTips = string.Format("手机存储空间不足，还缺: {0},请释放手机内存后重试", CommonHelper.FormatKB(diff));
                string strBtn = "更新";
                //EventMgr.onLaunchConfirmTips(strTips, strBtn, RealDownloadPatch, false);
                return;
            }
            Debug.LogFormat("确定下载补丁包,数量：{0}", m_lstDiffVersions.Count);
            ResetWorks();
            if (m_lockProgress == null)
            {
                m_lockProgress = new Object();
                m_lockMsg = new Object();
            }

            m_strErrMsg = "";
            m_haveDownedSize = 0;

            string strDownTips = "资源更新中";
            strDownTips = strDownTips + "(" + CommonHelper.FormatKB(m_totalSize) + ")";
            //EventMgr.onProgressTips.Invoke(strDownTips, true);

            FileHelper.Mkdir(AppConst.UpdateCachePath);
            for (int i = 0; i < m_lstDiffVersions.Count; i++)
            {
                ABFileInfo versionInfo = m_lstDiffVersions[i];
                string strURL = m_strDownloadUrl + versionInfo.filename;
                string strPath = AppConst.UpdateCachePath + "/" + versionInfo.filename;
                Debug.Log("添加任务，下载文件到：" + strPath);
                AddWork(new Downloader(strURL, strPath, OnDownloadFileProgress, OnDownloadFileEnd));
            }
            StartWork();
        }

        // 下载
        private void OnDownloadFileProgress(float progress, int downedSize, int totleSize, int diff)
        {
            lock (m_lockProgress)
            {
                m_haveDownedSize += diff;
            }
        }

        // 下载单个文件结束
        private void OnDownloadFileEnd(ErrorCode code, string msg, byte[] bytes)
        {
            if (code != ErrorCode.SUCCESS)
            {
                lock (m_lockMsg)
                {
                    m_strErrMsg += msg;
                    m_strErrMsg += "\n";
                    IsFail = true;
                }
            }
        }

        protected override void OnWorkProgress(float value)
        {
            // 大小没改变就不刷新进度
            if (m_lastDownedSize == m_haveDownedSize)
            {
                return;
            }

            m_lastDownedSize = m_haveDownedSize;
            float totalProgress = m_haveDownedSize * 1.0f / m_totalSize;

            //EventMgr.onProgressChange.Invoke(totalProgress);
        }

        protected override void OnWorkDone()
        {
            if (IsFail)
            {
                Debug.LogFormat("下载热更新资源失败，已下载:{0}，未下载:{1}, msg:{2}", CommonHelper.FormatKB(m_haveDownedSize), CommonHelper.FormatKB(m_totalSize), m_strErrMsg);
                float lackSize = m_totalSize - m_haveDownedSize;
                string strTips = string.Format("还有[{0}]资源未下载完成，请检查网络后继续下载", CommonHelper.FormatKB(lackSize));
                string strBtn = "继续";
                //EventMgr.onLaunchConfirmTips(strTips, strBtn, () =>
                //{
                //    // 重新计算差异再下载
                //    CalDiffToDownload();
                //}, false);
            }
            else
            {
                MoveFileToABPath();
                RemoveUselessFile();
                WriteVersion();
                m_onDownPatchDone(true);
            }
            //EventMgr.onProgressEnd.Invoke();
        }

        /// <summary>
        /// 把下载好的文件移动到沙盒AB路径下
        /// </summary>
        private void MoveFileToABPath()
        {

        }

        // 删除Update目录下无用的热更文件
        private void RemoveUselessFile()
        {
            if (!FileHelper.IsDirectoryExist(AppConst.UpdateCachePath))
            {
                return;
            }
            string[] arrOldFiles = FileHelper.GetAllChildFiles(AppConst.UpdateCachePath);
            for (int i = 0; i < arrOldFiles.Length; i++)
            {
                string strFullFileName = arrOldFiles[i];
                string strFileName = Path.GetFileName(strFullFileName);
                if (!m_dicSvrVersions.ContainsKey(strFileName))
                {
                    Debug.LogFormat("***RemoveUselessFile: {0}", strFullFileName);
                    FileHelper.DeleteFile(strFullFileName);
                }
            }
        }

        // 将版本号写入缓存
        public void WriteVersion()
        {
            CommonHelper.HotUpdateVersion = m_strNewVersion;
            FileHelper.WriteVersionInfo(AppConst.DataPath + "/" + AppConst.VersionFileName, m_dicSvrVersions);
            Debug.LogFormat("WriteVersion:{0}, strNewFileINfo:{1}", m_strNewVersion);
            PlayerPrefs.Save();
            m_dicSvrVersions.Clear();
            m_dicLocalVersions.Clear();
            m_lstDiffVersions.Clear();
        }
    }
}