using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;

namespace BruSoft.VS2P4
{
    using System.Drawing;


    using Process = System.Diagnostics.Process;

    /// <summary>
    /// This file contains the source control service implementation.
    /// The class implements the the IVsSccProvider interface that enables source control provider activation and switching.
    /// </summary>
    [Guid("8358dd60-10b0-478a-83b8-ea8ae3ecdaa2")]
    public class SccProviderService : 
        IVsSccProvider,             // Required for provider registration with source control manager
        IVsSccManager2,             // Base source control functionality interface
        IVsSccManagerTooltip,       // Provide tooltips for source control items
        IVsSolutionEvents,          // We'll register for solution events, these are useful for source control
        IVsSccGlyphs,               // For custom glyphs
        IVsSolutionEvents2,
        IVsQueryEditQuerySave2,     // Required to allow editing of controlled files 
        IVsTrackProjectDocumentsEvents2,  // Useful to track project changes (add, renames, deletes, etc)
        IDisposable
    {
        // Whether the provider is active or not
        private bool _isActive;

        // The service and source control provider
        private readonly VS2P4Package _sccProvider;

        // The cookie for solution events 
        private uint _vsSolutionEventsCookie;

        // The cookie for project document events
        private uint _tpdTrackProjectDocumentsCookie;

        // The list of files approved for in-memory edit
        private readonly Hashtable _approvedForInMemoryEdit = new Hashtable();

        /// <summary>
        /// The Perforce service used to interface to P4.Net.
        /// Marked internal for access by SccProviderServiceTest.cs
        /// </summary>
        internal P4Service P4Service;

        /// <summary>
        /// The Perforce file state cache
        /// </summary>
        private P4Cache _p4Cache;

        /// <summary>
        /// The options used by P4Service.
        /// </summary>
        internal P4Options Options { get; set; }

        /// <summary>
        /// The DTE2 object used to persist option settings between sessions.
        /// </summary>
        private EnvDTE80.DTE2 dte2 { get; set; }

        /// <summary>
        /// True means we are caching file states. 
        /// We allow false for simpler unit testing.
        /// </summary>
        private bool _isUsingP4Cache;

        /// <summary>
        /// Used for unit testing.
        /// </summary>
        public bool IsUsingP4Cache
        {
            get
            {
                return _isUsingP4Cache;
            }
            set
            {
                _isUsingP4Cache = value;
            }
        }

        /// <summary>
        /// The path to the currently-opened solution
        /// </summary>
        private string _solutionPath;

        /// <summary>
        /// The map of vsFileName to p4FileName, shared by P4Service and P4Cache
        /// </summary>
        private Map _map;

        /// <summary>
        /// true when a solution is open.
        /// </summary>
        internal bool IsSolutionLoaded;

        /// <summary>
        /// The selection and files we are removing.
        /// Set by OnQueryRemoveFiles
        /// May be deleted or not from Perforce in OnAfterRemovedFiles, depending on whether the user 
        ///     actually did delete the file (Remove, Cut) or didn't (Exclude From Project)
        /// </summary>
        private VsSelection vsSelectionFilesDeleted;

        #region SccProvider Service initialization/unitialization

        public SccProviderService(VS2P4Package sccProvider)
        {
            Debug.Assert(null != sccProvider);
            _sccProvider = sccProvider;

            // Subscribe to solution events
            var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
            sol.AdviseSolutionEvents(this, out _vsSolutionEventsCookie);
            Debug.Assert(VSConstants.VSCOOKIE_NIL != _vsSolutionEventsCookie);

            // Subscribe to project documents
            var tpdService = (IVsTrackProjectDocuments2)_sccProvider.GetService(typeof(SVsTrackProjectDocuments));
            tpdService.AdviseTrackProjectDocumentsEvents(this, out _tpdTrackProjectDocumentsCookie);
            Debug.Assert(VSConstants.VSCOOKIE_NIL != _tpdTrackProjectDocumentsCookie);
        }

        

        public void Dispose()
        {
            // Unregister from receiving solution events
            if (VSConstants.VSCOOKIE_NIL != _vsSolutionEventsCookie)
            {
                var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
                sol.UnadviseSolutionEvents(_vsSolutionEventsCookie);
                _vsSolutionEventsCookie = VSConstants.VSCOOKIE_NIL;
            }

            // Unregister from receiving project documents
            if (VSConstants.VSCOOKIE_NIL != _tpdTrackProjectDocumentsCookie)
            {
                var tpdService = (IVsTrackProjectDocuments2)_sccProvider.GetService(typeof(SVsTrackProjectDocuments));
                tpdService.UnadviseTrackProjectDocumentsEvents(_tpdTrackProjectDocumentsCookie);
                _tpdTrackProjectDocumentsCookie = VSConstants.VSCOOKIE_NIL;
            }

            // Also dispose of P4Service
            if (P4Service != null)
            {
                P4Service.Dispose();
            }

            if (_customSccGlyphsImageList != null)
            {
                _customSccGlyphsImageList.Dispose();
            }
        }

        #endregion

        //--------------------------------------------------------------------------------
        // IVsSccProvider specific functions
        //--------------------------------------------------------------------------------
        #region IVsSccProvider interface functions

        // Called by the scc manager when the provider is activated. 
        // Make visible and enable if necessary scc related menu commands
        public int SetActive()
        {
            Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture, "Provider set active"));

            _isActive = true;
            _sccProvider.OnActiveStateChange();

            // Load options persisted between sessions
            dte2 = (EnvDTE80.DTE2)_sccProvider.GetService(typeof(SDTE));
            Options = P4Options.Load(dte2);
            Log.OptionsLevel = Options.LogLevel;

            string solutionName = _sccProvider.GetSolutionFileName();
            if (solutionName != null)
            {
                // The solution was loaded before we were made active.
                IsSolutionLoaded = true;
            }

            if (IsSolutionLoaded)
            {
                // We are being activated after the solution was already opened.
                StartP4ServiceAndInitializeCache();
            }

            return VSConstants.S_OK;
        }


        // Called by the scc manager when the provider is deactivated. 
        // Hides and disable scc related menu commands
        public int SetInactive()
        {
            Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture, "Provider set inactive"));

            _isActive = false;
            _sccProvider.OnActiveStateChange();

            return VSConstants.S_OK;
        }


        public int AnyItemsUnderSourceControl(out int pfResult)
        {
            // Although the parameter is an int, it's in reality a BOOL value, so let's return 0/1 values
            if (!_isActive)
            {
                pfResult = 0;
            }
            else
            {
                pfResult = 1; // (_controlledProjects.Count != 0) ? 1 : 0;
            }
    
            return VSConstants.S_OK;
        }

        #endregion

        //--------------------------------------------------------------------------------
        // IVsSccManager2 specific functions
        //--------------------------------------------------------------------------------
        #region IVsSccManager2 interface functions

        public int BrowseForProject(out string pbstrDirectory, out int pfOK)
        {
            // Obsolete method
            pbstrDirectory = null;
            pfOK = 0;
            return VSConstants.E_NOTIMPL;
        }

        public int CancelAfterBrowseForProject() 
        {
            // Obsolete method
            return VSConstants.E_NOTIMPL;
        }

        /// <summary>
        /// Returns whether the source control provider is fully installed
        /// </summary>
        public int IsInstalled(out int pbInstalled)
        {
            // All source control packages should always return S_OK and set pbInstalled to nonzero
            pbInstalled = 1;
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Provide source control icons for the specified files and returns scc status of files
        /// </summary>
        /// <returns>The method returns S_OK if at least one of the files is controlled, S_FALSE if none of them are</returns>
        public int GetSccGlyph([InAttribute] int cFiles, [InAttribute] string[] rgpszFullPaths, [OutAttribute] VsStateIcon[] rgsiGlyphs, [OutAttribute] uint[] rgdwSccStatus)
        {
            Debug.Assert(cFiles == 1, "Only getting one file icon at a time is supported");

            // Return the icons and the status. While the status is a combination a flags, we'll return just values 
            // with one bit set, to make life easier for GetSccGlyphsFromStatus
            FileState status = GetFileState(rgpszFullPaths[0]);
            switch (status)
            {
                case FileState.CheckedInHeadRevision:
                case FileState.OpenForEditOtherUser:
                case FileState.LockedByOtherUser:
                    rgsiGlyphs[0] = (VsStateIcon)(_customSccGlyphBaseIndex + (uint)CustomSccGlyphs.CheckedIn);
                    if (rgdwSccStatus != null)
                    {
                        rgdwSccStatus[0] = (uint)__SccStatus.SCC_STATUS_CONTROLLED;
                    }
                    break;
                case FileState.OpenForEdit:
                    rgsiGlyphs[0] = VsStateIcon.STATEICON_CHECKEDOUT;
                    if (rgdwSccStatus != null)
                    {
                        rgdwSccStatus[0] = (uint)__SccStatus.SCC_STATUS_CHECKEDOUT;
                    }
                    break;
                case FileState.NotSet:
                    rgsiGlyphs[0] = VsStateIcon.STATEICON_EXCLUDEDFROMSCC;
                    if (rgdwSccStatus != null)
                    {
                        rgdwSccStatus[0] = (uint)__SccStatus.SCC_STATUS_CONTROLLED;
                    }
                    break;
                case FileState.NotInPerforce:
                    rgsiGlyphs[0] = VsStateIcon.STATEICON_BLANK;
                    if (rgdwSccStatus != null)
                    {
                        rgdwSccStatus[0] = (uint)__SccStatus.SCC_STATUS_NOTCONTROLLED;
                    }
                    break;
                case FileState.OpenForEditDiffers:
                    rgsiGlyphs[0] = (VsStateIcon)(_customSccGlyphBaseIndex + (uint)CustomSccGlyphs.Differs);
                    if (rgdwSccStatus != null)
                    {
                        rgdwSccStatus[0] = (uint)__SccStatus.SCC_STATUS_CHECKEDOUT;
                    }
                    break;
                case FileState.CheckedInPreviousRevision:
                    rgsiGlyphs[0] = (VsStateIcon)(_customSccGlyphBaseIndex + (uint)CustomSccGlyphs.Differs);
                    if (rgdwSccStatus != null)
                    {
                        rgdwSccStatus[0] = (uint)__SccStatus.SCC_STATUS_CONTROLLED;
                    }
                    break;
                case FileState.OpenForDelete:
                case FileState.OpenForDeleteOtherUser:
                case FileState.DeletedAtHeadRevision:
                case FileState.OpenForRenameSource:
                    rgsiGlyphs[0] = VsStateIcon.STATEICON_DISABLED;
                    if (rgdwSccStatus != null)
                    {
                        rgdwSccStatus[0] = (uint)__SccStatus.SCC_STATUS_CONTROLLED;
                    }
                    break;
                case FileState.OpenForAdd:
                case FileState.OpenForRenameTarget:
                    rgsiGlyphs[0] = (VsStateIcon)(_customSccGlyphBaseIndex + (uint)CustomSccGlyphs.Add);
                    if (rgdwSccStatus != null)
                    {
                        rgdwSccStatus[0] = (uint)__SccStatus.SCC_STATUS_CHECKEDOUT;
                    }
                    break;
                case FileState.NeedsResolved:
                case FileState.OpenForIntegrate:
                    rgsiGlyphs[0] = (VsStateIcon)(_customSccGlyphBaseIndex + (uint)CustomSccGlyphs.Resolve);
                    if (rgdwSccStatus != null)
                    {
                        rgdwSccStatus[0] = (uint)__SccStatus.SCC_STATUS_CHECKEDOUT;
                    }
                    break;
                case FileState.Locked:
                    rgsiGlyphs[0] = VsStateIcon.STATEICON_READONLY;
                    if (rgdwSccStatus != null)
                    {
                        rgdwSccStatus[0] = (uint)__SccStatus.SCC_STATUS_CHECKEDOUT;
                    }
                    break;
                case FileState.OpenForBranch:
                    rgsiGlyphs[0] = VsStateIcon.STATEICON_ORPHANED;
                    if (rgdwSccStatus != null)
                    {
                        rgdwSccStatus[0] = (uint)__SccStatus.SCC_STATUS_CHECKEDOUT;
                    }
                    break;
                default:
                    // This is an uncontrolled file, return a blank scc glyph for it
                    rgsiGlyphs[0] = VsStateIcon.STATEICON_BLANK;
                    if (rgdwSccStatus != null)
                    {
                        rgdwSccStatus[0] = (uint)__SccStatus.SCC_STATUS_NOTCONTROLLED;
                    }
                    break;
            }

            return VSConstants.S_OK;
        }

       

        /// <summary>
        /// Determines the corresponding scc status glyph to display, given a combination of scc status flags.
        /// DAB: AFAIK this method is NEVER called.
        /// </summary>
        public int GetSccGlyphFromStatus([InAttribute] uint dwSccStatus, [OutAttribute] VsStateIcon[] psiGlyph)
        {
            switch (dwSccStatus)
            {
                case (uint) __SccStatus.SCC_STATUS_CHECKEDOUT:
                    psiGlyph[0] = VsStateIcon.STATEICON_CHECKEDOUT;
                    break;
                case (uint) __SccStatus.SCC_STATUS_CONTROLLED:
                    psiGlyph[0] = (VsStateIcon)(_customSccGlyphBaseIndex + (uint)CustomSccGlyphs.CheckedIn); // VsStateIcon.STATEICON_CHECKEDIN;
                    break;
                default:
                    // Uncontrolled
                    psiGlyph[0] = VsStateIcon.STATEICON_BLANK;
                    break;
            }
            return VSConstants.S_OK;
        }

        /// <summary>
        /// One of the most important methods in a source control provider, is called by projects that are under source control when they are first opened to register project settings
        /// </summary>
        public int RegisterSccProject([InAttribute] IVsSccProject2 pscp2Project, [InAttribute] string pszSccProjectName, [InAttribute] string pszSccAuxPath, [InAttribute] string pszSccLocalPath, [InAttribute] string pszProvider)
        {
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Called by projects registered with the source control portion of the environment before they are closed. 
        /// </summary>
        public int UnregisterSccProject([InAttribute] IVsSccProject2 pscp2Project)
        {
            return VSConstants.S_OK;
        }

        #endregion

        //--------------------------------------------------------------------------------
        // IVsSccManagerTooltip specific functions
        //--------------------------------------------------------------------------------
        #region IVsSccManagerTooltip interface functions

        /// <summary>
        /// Called by solution explorer to provide tooltips for items. Returns a text describing the source control status of the item.
        /// </summary>
        public int GetGlyphTipText([InAttribute] IVsHierarchy phierHierarchy, [InAttribute] uint itemidNode, out string pbstrTooltipText)
        {
            // Initialize output parameters
            pbstrTooltipText = "";

            IList<string> files = _sccProvider.GetNodeFiles(phierHierarchy, itemidNode);
            if (files.Count == 0)
            {
                return VSConstants.S_OK;
            }

            // Return the glyph text based on the first file of node (the master file)
            FileState status = GetFileState(files[0]);
            switch (status)
            {
                case FileState.NotSet: // Don't know yet if this is controlled.
                case FileState.NotInPerforce: // Uncontrolled files don't have tooltips.
                    pbstrTooltipText = "";
                    break;
                case FileState.OpenForEdit:
                    pbstrTooltipText = Resources.State_OpenForEdit;
                    break;
                case FileState.OpenForEditOtherUser:
                    pbstrTooltipText = Resources.State_OpenForEditOtherUser;
                    break;
                case FileState.OpenForEditDiffers:
                    pbstrTooltipText = Resources.State_OpenForEditDiffers;
                    break;
                case FileState.Locked:
                    pbstrTooltipText = Resources.State_Locked;
                    break;
                case FileState.LockedByOtherUser:
                    pbstrTooltipText = Resources.State_LockedByOtherUser;
                    break;
                case FileState.OpenForDelete:
                    pbstrTooltipText = Resources.State_OpenForDelete;
                    break;
                case FileState.OpenForDeleteOtherUser:
                    pbstrTooltipText = Resources.State_OpenForDeleteOtherUser;
                    break;
                case FileState.DeletedAtHeadRevision:
                    pbstrTooltipText = Resources.State_DeletedAtHeadRevision;
                    break;
                case FileState.OpenForAdd:
                    pbstrTooltipText = Resources.State_OpenForAdd;
                    break;
                case FileState.OpenForRenameSource:
                    pbstrTooltipText = Resources.State_OpenForRenameSource;
                    break;
                case FileState.OpenForRenameTarget:
                    pbstrTooltipText = Resources.State_OpenForRenameTarget;
                    break;
                case FileState.CheckedInHeadRevision:
                    pbstrTooltipText = Resources.State_CheckedInHeadRevision;
                    break;
                case FileState.CheckedInPreviousRevision:
                    pbstrTooltipText = Resources.State_CheckedInPreviousRevision;
                    break;
                case FileState.NeedsResolved:
                    pbstrTooltipText = Resources.State_NeedsResolved;
                    break;
                case FileState.OpenForBranch:
                    pbstrTooltipText = Resources.State_OpenForBranch;
                    break;
                case FileState.OpenForIntegrate:
                    pbstrTooltipText = Resources.State_OpenForIntegrate;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return VSConstants.S_OK;
        }

        #endregion

        //--------------------------------------------------------------------------------
        // IVsSolutionEvents and IVsSolutionEvents2 specific functions
        //--------------------------------------------------------------------------------
        #region IVsSolutionEvents interface functions

        public int OnAfterCloseSolution([InAttribute] Object pUnkReserved)
        {
            P4Service = null;
            if (_p4Cache != null)
            {
                _p4Cache.P4CacheUpdated -= P4CacheUpdated;
                _p4Cache = null;
            }
            _isUsingP4Cache = false;
            IsSolutionLoaded = false;
            _approvedForInMemoryEdit.Clear();

            return VSConstants.S_OK;
        }

        public int OnAfterLoadProject([InAttribute] IVsHierarchy pStubHierarchy, [InAttribute] IVsHierarchy pRealHierarchy)
        {
            // If a project is reloaded in the solution after the solution was opened, do Refresh to pick up file states for the added project
            if (_isActive && IsSolutionLoaded)
            {
                Log.Information("After opening project, refreshing all solution glyphs");
                Refresh();
            }

            return VSConstants.S_OK;
        }

        public int OnAfterOpenProject([InAttribute] IVsHierarchy pHierarchy, [InAttribute] int fAdded)
        {
            // If a project is added to the solution after the solution was opened, do Refresh to pick up file states for the added project
            if (_isActive && IsSolutionLoaded && fAdded == 1)
            {
                Log.Information("After opening project, refreshing all solution glyphs");
                Refresh();
            }

            return VSConstants.S_OK;
        }

        public int OnAfterOpenSolution([InAttribute] Object pUnkReserved, [InAttribute] int fNewSolution)
        {
            if (_isActive)
            {
                StartP4ServiceAndInitializeCache();
            }

            IsSolutionLoaded = true;

            return VSConstants.S_OK;
        }

        private void StartP4ServiceAndInitializeCache()
        {
            // Before we set up the connection to Perforce, set the CWD so we pick up any changes to P4Config
            // We assume that the solution is in the right workspace

            _map = new Map();
            string solutionName = _sccProvider.GetSolutionFileName();
            if (solutionName != null)
            {
                _solutionPath = Path.GetDirectoryName(solutionName);
                P4Service = new P4Service(Options.Server, Options.User, Options.Password, Options.Workspace, Options.UseP4Config, _solutionPath, _map);
            }

            _p4Cache = new P4Cache(Options.Server, Options.User, Options.Password, Options.Workspace, Options.UseP4Config, _solutionPath, _map);
            _p4Cache.P4CacheUpdated += P4CacheUpdated;

            VsSelection vsSelection = _sccProvider.GetSolutionSelection();
            _p4Cache.Initialize(vsSelection);
            _isUsingP4Cache = true;
        }

        private void P4CacheUpdated(object sender, P4CacheEventArgs e)
        {
            IList<string> fileNames = e.VsSelection.FileNames;
            IList<VSITEMSELECTION> nodes = e.VsSelection.Nodes;
            Log.Debug(String.Format("SccProviderService.P4CacheUpdated Starting: Updating all glyphs, for {0} files and {1} nodes", fileNames.Count, nodes.Count));

            NodesGlyphsRefresher nodesGlyphsRefresher = new NodesGlyphsRefresher(nodes, _sccProvider);
            nodesGlyphsRefresher.Refresh();
            Log.Debug("Finished P4CacheUpdated");
        }

        public int OnBeforeCloseProject([InAttribute] IVsHierarchy pHierarchy, [InAttribute] int fRemoved)
        {
            return VSConstants.S_OK;
        }

        public int OnBeforeCloseSolution([InAttribute] Object pUnkReserved)
        {
            // Since we registered the solution with source control from OnAfterOpenSolution, it would be nice to unregister it, too, when it gets closed.
            // Also, unregister the solution folders
            Hashtable enumSolFolders = _sccProvider.GetSolutionFoldersEnum();
            foreach (IVsHierarchy pHier in enumSolFolders.Keys)
            {
                IVsSccProject2 pSccProject = pHier as IVsSccProject2;
                if (pSccProject != null)
                {
                    UnregisterSccProject(pSccProject);
                }
            }

            UnregisterSccProject(null);

            return VSConstants.S_OK;
        }

        public int OnBeforeUnloadProject([InAttribute] IVsHierarchy pRealHierarchy, [InAttribute] IVsHierarchy pStubHierarchy)
        {
            return VSConstants.S_OK;
        }

        public int OnQueryCloseProject([InAttribute] IVsHierarchy pHierarchy, [InAttribute] int fRemoving, [InAttribute] ref int pfCancel)
        {
            return VSConstants.S_OK;
        }

        public int OnQueryCloseSolution([InAttribute] Object pUnkReserved, [InAttribute] ref int pfCancel)
        {
            return VSConstants.S_OK;
        }

        public int OnQueryUnloadProject([InAttribute] IVsHierarchy pRealHierarchy, [InAttribute] ref int pfCancel)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterMergeSolution ([InAttribute] Object pUnkReserved )
        {
            // reset the flag now that solutions were merged and the merged solution completed opening
            //_loadingControlledSolutionLocation = "";

            return VSConstants.S_OK;
        }

        #endregion

        //--------------------------------------------------------------------------------
        // IVsQueryEditQuerySave2 specific functions
        //--------------------------------------------------------------------------------
        #region IVsQueryEditQuerySave2 interface functions

        public int BeginQuerySaveBatch ()
        {
            return VSConstants.S_OK;
        }

        public int EndQuerySaveBatch ()
        {
            return VSConstants.S_OK;
        }

        public int DeclareReloadableFile([InAttribute] string pszMkDocument, [InAttribute] uint rgf, [InAttribute] VSQEQS_FILE_ATTRIBUTE_DATA[] pFileInfo)
        {
            return VSConstants.S_OK;
        }

        public int DeclareUnreloadableFile([InAttribute] string pszMkDocument, [InAttribute] uint rgf, [InAttribute] VSQEQS_FILE_ATTRIBUTE_DATA[] pFileInfo)
        {
            return VSConstants.S_OK;
        }

        public int IsReloadable ([InAttribute] string pszMkDocument, out int pbResult )
        {
            // Since we're not tracking which files are reloadable and which not, consider everything reloadable
            pbResult = 1;
            return VSConstants.S_OK;
        }

        public int OnAfterSaveUnreloadableFile([InAttribute] string pszMkDocument, [InAttribute] uint rgf, [InAttribute] VSQEQS_FILE_ATTRIBUTE_DATA[] pFileInfo)
        {
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Called by projects and editors before modifying a file
        /// The function allows the source control systems to take the necessary actions (checkout, flip attributes)
        /// to make the file writable in order to allow the edit to continue
        ///
        /// There are a lot of cases to deal with during QueryEdit/QuerySave. 
        /// - called in commmand line mode, when UI cannot be displayed
        /// - called during builds, when save shoudn't probably be allowed
        /// - called during projects migration, when projects are not open and not registered yet with source control
        /// - checking out files may bring new versions from vss database which may be reloaded and the user may lose in-memory changes; some other files may not be reloadable
        /// - not all editors call QueryEdit when they modify the file the first time (buggy editors!), and the files may be already dirty in memory when QueryEdit is called
        /// - files on disk may be modified outside IDE and may have attributes incorrect for their scc status
        /// - checkouts may fail
        /// The sample provider won't deal with all these situations, but a real source control provider should!
        /// </summary>
        public int QueryEditFiles([InAttribute] uint rgfQueryEdit, [InAttribute] int cFiles, [InAttribute] string[] rgpszMkDocuments, [InAttribute] uint[] rgrgf, [InAttribute] VSQEQS_FILE_ATTRIBUTE_DATA[] rgFileInfo, out uint pfEditVerdict, out uint prgfMoreInfo)
        {
            // Initialize output variables
            pfEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
            prgfMoreInfo = 0;

            // In non-UI mode just allow the edit, because the user cannot be asked what to do with the file
            if (_sccProvider.InCommandLineMode())
            {
                return VSConstants.S_OK;
            }

            VsSelection vsSelectionToCheckOut = GetVsSelectionNoFileNamesAllNodes();

            try 
            {
                //Iterate through all the files
                for (int iFile = 0; iFile < cFiles; iFile++)
                {

                    uint fEditVerdict = (uint)tagVSQueryEditResult.QER_EditNotOK;
                    uint fMoreInfo = 0;
                    string fileName = rgpszMkDocuments[iFile];

                    // Because of the way we calculate the status, it is not possible to have a 
                    // checked in file that is writeable on disk, or a checked out file that is read-only on disk
                    // A source control provider would need to deal with those situations, too
                    FileState state = GetFileState(fileName);
                    bool fileExists = File.Exists(fileName);
                    bool isFileReadOnly = false;
                    if (fileExists)
                    {
                        isFileReadOnly = ((File.GetAttributes(fileName) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
                    }

                    // Allow the edits if the file does not exist or is writable
                    if (!fileExists || !isFileReadOnly)
                    {
                        fEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
                    }
                    else
                    {
                        // If the IDE asks about a file that was already approved for in-memory edit, allow the edit without asking the user again
                        if (_approvedForInMemoryEdit.ContainsKey(fileName.ToLower()))
                        {
                            fEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
                            fMoreInfo = (uint)(tagVSQueryEditResultFlags.QER_InMemoryEdit);
                        }
                        else
                        {
                            switch (state)
                            {
                                case FileState.CheckedInHeadRevision:
                                case FileState.CheckedInPreviousRevision:
                                    fMoreInfo = QueryEditFileCheckedIn(vsSelectionToCheckOut, fileName, rgfQueryEdit, ref fEditVerdict);
                                    break;
                                case FileState.OpenForEdit:
                                case FileState.OpenForAdd:
                                case FileState.OpenForEditDiffers:
                                case FileState.OpenForRenameTarget:
                                case FileState.NotInPerforce:
                                    fMoreInfo = QueryEditFileNotCheckedIn(fileName, rgfQueryEdit, ref fEditVerdict);
                                    break;
                                case FileState.NotSet:
                                    // We must ignore files that are .NotSet -- no way to determine what to do.
                                    break;
                                default:
                                    throw new ArgumentOutOfRangeException(state.ToString());
                            }
                        }
                    }

                    // It's a bit unfortunate that we have to return only one set of flags for all the files involved in the operation
                    // The edit can continue if all the files were approved for edit
                    prgfMoreInfo |= fMoreInfo;
                    pfEditVerdict |= fEditVerdict;

                    if (pfEditVerdict == (uint)tagVSQueryEditResult.QER_EditOK && vsSelectionToCheckOut.FileNames.Count > 0)
                    {
                        CheckoutFiles(vsSelectionToCheckOut);
                    }
                }
            }
            catch(Exception ex)
            {
                // If an exception was caught, do not allow the edit
                pfEditVerdict = (uint)tagVSQueryEditResult.QER_EditNotOK;
                prgfMoreInfo = (uint)tagVSQueryEditResultFlags.QER_EditNotPossible;
                Log.Error("SccProviderService: QueryEditFiles: " + ex.Message);
            }

            return VSConstants.S_OK;
        }

        /// <summary>
        /// QueryEdit and QuerySave and OnAfterRenameFiles (and maybe others) may change the state of a file but we don't know what nodes to update.
        /// So we start here with an empty list of fileNames but with all nodes in the solution.
        /// TODO: Figure out a way to discover the node(s) that use a particular fileName, and just update those nodes.
        /// </summary>
        /// <returns></returns>
        private VsSelection GetVsSelectionNoFileNamesAllNodes()
        {
            VsSelection vsSelection = new VsSelection(new List<string>(), _sccProvider.GetSolutionNodes());
            return vsSelection;
        }

        /// <summary>
        /// This method is called only for readonly files that are checked in.
        /// If the user agrees (or AutoEdit), add fileName to vsSelectionToCheckOut
        /// </summary>
        /// <param name="vsSelectionToCheckOut">a list of fileNames to check out</param>
        /// <param name="fileName">The fileName we're checking.</param>
        /// <param name="rgfQueryEdit">The kind of query.</param>
        /// <param name="fEditVerdict">The flag whether or not edit will be allowed.</param>
        /// <returns>More Info flag.</returns>
        private uint QueryEditFileCheckedIn(VsSelection vsSelectionToCheckOut, string fileName, uint rgfQueryEdit, ref uint fEditVerdict)
        {
            uint fMoreInfo;
            if ((rgfQueryEdit & (uint)tagVSQueryEditFlags.QEF_ReportOnly) != 0)
            {
                // The file is checked in and ReportOnly means we can't ask the user anything. The answer is "No."
                fMoreInfo = (uint)(tagVSQueryEditResultFlags.QER_EditNotPossible | tagVSQueryEditResultFlags.QER_ReadOnlyUnderScc);
            }
            else
            {
                if (Options.AutoCheckoutOnEdit)
                {
                    // Add this file to the list to be checked out.
                    vsSelectionToCheckOut.FileNames.Add(fileName);
                    fEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
                    fMoreInfo = (uint)tagVSQueryEditResultFlags.QER_MaybeCheckedout;
                }
                else
                {
                    var dlgAskCheckout = new DlgQueryEditCheckedInFile(fileName);
                    if ((rgfQueryEdit & (uint)tagVSQueryEditFlags.QEF_SilentMode) != 0)
                    {
                        // When called in silent mode, attempt the checkout
                        // (The alternative is to deny the edit and return QER_NoisyPromptRequired and expect for a non-silent call)
                        dlgAskCheckout.Answer = DlgQueryEditCheckedInFile.qecifCheckout;
                    }
                    else
                    {
                        dlgAskCheckout.ShowDialog();
                    }

                    if (dlgAskCheckout.Answer == DlgQueryEditCheckedInFile.qecifCheckout)
                    {
                        // Add this file to the list to be checked out.
                        vsSelectionToCheckOut.FileNames.Add(fileName);
                        fEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
                        fMoreInfo = (uint)tagVSQueryEditResultFlags.QER_MaybeCheckedout;
                        // Do not forget to set QER_Changed if the content of the file on disk changes during the query edit
                        // Do not forget to set QER_Reloaded if the source control reloads the file from disk after such changing checkout.
                    }
                    else if (dlgAskCheckout.Answer == DlgQueryEditCheckedInFile.qecifEditInMemory)
                    {
                        // Allow edit in memory
                        fEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
                        fMoreInfo = (uint)(tagVSQueryEditResultFlags.QER_InMemoryEdit);
                        // Add the file to the list of files approved for edit, so if the IDE asks again about this file, we'll allow the edit without asking the user again
                        // UNDONE: Currently, a file gets removed from _approvedForInMemoryEdit list only when the solution is closed. Consider intercepting the 
                        // IVsRunningDocTableEvents.OnAfterSave/OnAfterSaveAll interface and removing the file from the approved list after it gets saved once.
                        _approvedForInMemoryEdit[fileName.ToLower()] = true;
                    }
                    else
                    {
                        fEditVerdict = (uint)tagVSQueryEditResult.QER_NoEdit_UserCanceled;
                        fMoreInfo = (uint)(tagVSQueryEditResultFlags.QER_ReadOnlyUnderScc | tagVSQueryEditResultFlags.QER_CheckoutCanceledOrFailed);
                    }
                    dlgAskCheckout.Dispose();
                }
            }
            return fMoreInfo;
        }

        /// <summary>
        /// This method is called only for readonly files that are not controlled or are already checked out.
        /// If the user agrees, make it writeable.
        /// </summary>
        /// <param name="fileName">The fileName we're checking.</param>
        /// <param name="rgfQueryEdit">The kind of query.</param>
        /// <param name="fEditVerdict">The flag whether or not edit will be allowed.</param>
        /// <returns>More Info flag.</returns>
        private uint QueryEditFileNotCheckedIn(string fileName, uint rgfQueryEdit, ref uint fEditVerdict)
        {
            uint fMoreInfo = 0;
            if ((rgfQueryEdit & (uint)tagVSQueryEditFlags.QEF_ReportOnly) != 0)
            {
                fMoreInfo = (uint)(tagVSQueryEditResultFlags.QER_EditNotPossible | tagVSQueryEditResultFlags.QER_ReadOnlyNotUnderScc);
            }
            else
            {
                bool allowMakeFileWritable = false;
                if ((rgfQueryEdit & (uint)tagVSQueryEditFlags.QEF_SilentMode) != 0)
                {
                    // When called in silent mode, deny the edit and return QER_NoisyPromptRequired and expect for a non-silent call)
                    // (The alternative is to silently make the file writable and accept the edit)
                    fMoreInfo = (uint)(tagVSQueryEditResultFlags.QER_EditNotPossible | 
                        tagVSQueryEditResultFlags.QER_ReadOnlyNotUnderScc |
                        tagVSQueryEditResultFlags.QER_NoisyPromptRequired);
                }
                else
                {
                    // This is a read-only file, warn the user
                    string messageText = Resources.QEQS_EditUncontrolledReadOnly;
                    allowMakeFileWritable = PromptForAllowMakeFileWritable(messageText, fileName);
                }

                if (allowMakeFileWritable)
                {
                    // Make the file writable and allow the edit
                    File.SetAttributes(fileName, FileAttributes.Normal);
                    fEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
                }
            }
            return fMoreInfo;
        }

        /// <summary>
        /// Ask user if it's okay to make this file writable. 
        /// </summary>
        /// <param name="messageText">The prompt text.</param>
        /// <param name="fileName">The filename.</param>
        /// <returns>true if user says "Okay"</returns>
        private bool PromptForAllowMakeFileWritable(string messageText, string fileName)
        {
            bool allowMakeFileWritable = false;
            var uiShell = (IVsUIShell)_sccProvider.GetService(typeof(SVsUIShell));
            Guid clsid = Guid.Empty;
            int result;
            string messageCaption = Resources.ProviderName;
            if (uiShell.ShowMessageBox(
                    0,
                    ref clsid,
                    messageCaption,
                    String.Format(CultureInfo.CurrentUICulture, messageText, fileName),
                    string.Empty,
                    0,
                    OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND,
                    OLEMSGICON.OLEMSGICON_QUERY,
                    0,
                        // false = application modal; true would make it system modal
                    out result) == VSConstants.S_OK && result == (int)DialogResult.Yes)
            {
                allowMakeFileWritable = true;
            }
            return allowMakeFileWritable;
        }

        /// <summary>
        /// Called by editors and projects before saving the files
        /// The function allows the source control systems to take the necessary actions (checkout, flip attributes)
        /// to make the file writable in order to allow the file saving to continue
        /// </summary>
        public int QuerySaveFile([InAttribute] string pszMkDocument, [InAttribute] uint rgf, [InAttribute] VSQEQS_FILE_ATTRIBUTE_DATA[] pFileInfo, out uint pdwQSResult)
        {
            // Delegate to the other QuerySave function
            string[] rgszDocuements = new string[1];
            uint[] rgrgf = new uint[1];
            rgszDocuements[0] = pszMkDocument;
            rgrgf[0] = rgf;
            return QuerySaveFiles(rgf, 1, rgszDocuements, rgrgf, pFileInfo, out pdwQSResult);
        }

        /// <summary>
        /// Called by editors and projects before saving the files
        /// The function allows the source control systems to take the necessary actions (checkout, flip attributes)
        /// to make the file writable in order to allow the file saving to continue
        /// </summary>
        public int QuerySaveFiles([InAttribute] uint rgfQuerySave, [InAttribute] int cFiles, [InAttribute] string[] rgpszMkDocuments, [InAttribute] uint[] rgrgf, [InAttribute] VSQEQS_FILE_ATTRIBUTE_DATA[] rgFileInfo, out uint pdwQSResult)
        {
            // Initialize output variables
            // It's a bit unfortunate that we have to return only one set of flags for all the files involved in the operation
            // The last file will win setting this flag
            pdwQSResult = (uint)tagVSQuerySaveResult.QSR_SaveOK;

            // In non-UI mode attempt to silently flip the attributes of files or check them out 
            // and allow the save, because the user cannot be asked what to do with the file
            if (_sccProvider.InCommandLineMode())
            {
                rgfQuerySave = rgfQuerySave | (uint)tagVSQuerySaveFlags.QSF_SilentMode;
            }

            VsSelection vsSelection = GetVsSelectionNoFileNamesAllNodes();
            try 
            {
                for (int iFile = 0; iFile < cFiles; iFile++)
                {
                    string fileName = rgpszMkDocuments[iFile];
                    FileState state = GetFileState(fileName);
                    bool fileExists = File.Exists(fileName);
                    bool isFileReadOnly = false;
                    if (fileExists)
                    {
                        isFileReadOnly = ((File.GetAttributes(fileName) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
                    }

                    switch (state)
                    {
                        case FileState.CheckedInHeadRevision:
                        case FileState.CheckedInPreviousRevision:
                            if (Options.AutoCheckoutOnSave)
                            {
                                vsSelection.FileNames.Add(fileName);
                                pdwQSResult = (uint)tagVSQuerySaveResult.QSR_SaveOK;
                                break;
                            }

                            var dlgAskCheckout = new DlgQuerySaveCheckedInFile(fileName);
                            if ((rgfQuerySave & (uint)tagVSQuerySaveFlags.QSF_SilentMode) != 0)
                            {
                                // When called in silent mode, attempt the checkout
                                // (The alternative is to deny the save, return QSR_NoSave_NoisyPromptRequired and expect for a non-silent call)
                                dlgAskCheckout.Answer = DlgQuerySaveCheckedInFile.qscifCheckout;
                            }
                            else
                            {
                                dlgAskCheckout.ShowDialog();
                            }

                            switch (dlgAskCheckout.Answer)
                            {
                                case DlgQueryEditCheckedInFile.qecifCheckout:
                                    vsSelection.FileNames.Add(fileName);
                                    pdwQSResult = (uint)tagVSQuerySaveResult.QSR_SaveOK;
                                    break;
                                case DlgQuerySaveCheckedInFile.qscifForceSaveAs:
                                    pdwQSResult = (uint)tagVSQuerySaveResult.QSR_ForceSaveAs;
                                    break;
                                case DlgQuerySaveCheckedInFile.qscifSkipSave:
                                    pdwQSResult = (uint)tagVSQuerySaveResult.QSR_NoSave_Continue;
                                    break;
                                default:
                                    pdwQSResult = (uint)tagVSQuerySaveResult.QSR_NoSave_Cancel;
                                    break;
                            }
                            dlgAskCheckout.Dispose();
                            break;
                        case FileState.OpenForEdit:
                        case FileState.OpenForAdd:
                        case FileState.OpenForEditDiffers:
                        case FileState.OpenForRenameTarget:
                        case FileState.NotSet:
                        case FileState.NotInPerforce:
                            if (fileExists && isFileReadOnly)
                            {
                                // This is a read-only file, warn the user
                                string messageText = Resources.QEQS_SaveReadOnly;
                                bool allowMakeFileWritable = PromptForAllowMakeFileWritable(messageText, fileName);
                                if (!allowMakeFileWritable)
                                {
                                    pdwQSResult = (uint)tagVSQuerySaveResult.QSR_NoSave_Continue;
                                    break;
                                }

                                // Make the file writable and allow the save
                                File.SetAttributes(fileName, FileAttributes.Normal);
                            }

                            // Allow the save now 
                            pdwQSResult = (uint)tagVSQuerySaveResult.QSR_SaveOK;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                // If an exception was caught, do not allow the save
                pdwQSResult = (uint)tagVSQuerySaveResult.QSR_NoSave_Cancel;
                Log.Error(String.Format("SccProviderService.QuerySaveFiles() Exception: {0}", ex.Message));
            }

            if (vsSelection.FileNames.Count > 0)
            {
                CheckoutFiles(vsSelection);
            }
     
            return VSConstants.S_OK;
        }

        #endregion

        //--------------------------------------------------------------------------------
        // IVsTrackProjectDocumentsEvents2 specific functions
        //--------------------------------------------------------------------------------

        public int OnQueryAddFiles([InAttribute] IVsProject pProject, [InAttribute] int cFiles, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSQUERYADDFILEFLAGS[] rgFlags, [OutAttribute] VSQUERYADDFILERESULTS[] pSummaryResult, [OutAttribute] VSQUERYADDFILERESULTS[] rgResults)
        {
            return VSConstants.E_NOTIMPL;
        }

        /// <summary>
        /// Implement this function to update the project scc glyphs when the items are added to the project.
        /// If a project doesn't call GetSccGlyphs as they should do (as solution folder do), this will update the glyphs correctly when the project is controlled
        /// </summary>
        public int OnAfterAddFilesEx([InAttribute] int cProjects, [InAttribute] int cFiles, [InAttribute] IVsProject[] rgpProjects, [InAttribute] int[] rgFirstIndices, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSADDFILEFLAGS[] rgFlags)
        {
            VsSelection vsSelection = GetVsSelectionNoFileNamesAllNodes();

            // Start by iterating through all projects calling this function
            for (int iProject = 0; iProject < cProjects; iProject++)
            {
                IVsSccProject2 sccProject = rgpProjects[iProject] as IVsSccProject2;

                // If the project is not controllable, or is not controlled, skip it
                if (sccProject == null)
                {
                    continue;
                }

                // Files in this project are in rgszMkOldNames, rgszMkNewNames arrays starting with iProjectFilesStart index and ending at iNextProjecFilesStart-1
                int iProjectFilesStart = rgFirstIndices[iProject];
                int iNextProjectFilesStart = cFiles;
                if (iProject < cProjects - 1)
                {
                    iNextProjectFilesStart = rgFirstIndices[iProject + 1];
                }


                // Now that we know which files belong to this project, iterate the project files
                for (int iFile = iProjectFilesStart; iFile < iNextProjectFilesStart; iFile++)
                {
                    string fileName = rgpszMkDocuments[iFile];
                    if (Options.AutoAdd)
                    {
                        vsSelection.FileNames.Add(fileName);
                        continue;
                    }

                    string msg = String.Format(Resources.Add_filename_to_Perforce, fileName);
                    DialogResult dialogResult = MessageBox.Show(msg, Resources.Add_File, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dialogResult == DialogResult.Yes)
                    {
                        vsSelection.FileNames.Add(fileName);
                    }
                }

            }

            if (vsSelection.FileNames.Count > 0)
            {
                AddFiles(vsSelection);
            }

            return VSConstants.E_NOTIMPL;
        }

        public int OnQueryAddDirectories ([InAttribute] IVsProject pProject, [InAttribute] int cDirectories, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSQUERYADDDIRECTORYFLAGS[] rgFlags, [OutAttribute] VSQUERYADDDIRECTORYRESULTS[] pSummaryResult, [OutAttribute] VSQUERYADDDIRECTORYRESULTS[] rgResults)
        {
            return VSConstants.E_NOTIMPL;
        }

        public int OnAfterAddDirectoriesEx ([InAttribute] int cProjects, [InAttribute] int cDirectories, [InAttribute] IVsProject[] rgpProjects, [InAttribute] int[] rgFirstIndices, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSADDDIRECTORYFLAGS[] rgFlags)
        {
            return VSConstants.E_NOTIMPL;
        }



        /// <summary>
        /// Implement OnQueryRemoveFiles event to warn the user when he's deleting controlled files.
        /// The user gets the chance to cancel the file removal.
        /// This routine only builds the list of files that MIGHT be removed from Perforce.
        /// </summary>
        public int OnQueryRemoveFiles([InAttribute] IVsProject pProject, [InAttribute] int cFiles, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSQUERYREMOVEFILEFLAGS[] rgFlags, [OutAttribute] VSQUERYREMOVEFILERESULTS[] pSummaryResult, [OutAttribute] VSQUERYREMOVEFILERESULTS[] rgResults)
        {
            pSummaryResult[0] = VSQUERYREMOVEFILERESULTS.VSQUERYREMOVEFILERESULTS_RemoveOK;
            if (rgResults != null)
            {
                for (int iFile = 0; iFile < cFiles; iFile++)
                {
                    rgResults[iFile] = VSQUERYREMOVEFILERESULTS.VSQUERYREMOVEFILERESULTS_RemoveOK;
                }
            }

            vsSelectionFilesDeleted = GetVsSelectionNoFileNamesAllNodes();
            try
            {
                var sccProject = pProject as IVsSccProject2;
                string projectName;
                if (sccProject == null)
                {
                    // This is the solution calling
                    projectName = _sccProvider.GetSolutionFileName();
                }
                else
                {
                    // If the project doesn't support source control, it will be skipped
                    projectName = _sccProvider.GetProjectFileName(sccProject);
                }

                if (projectName != null)
                {
                    for (int iFile = 0; iFile < cFiles; iFile++)
                    {
                        string fileName = rgpszMkDocuments[iFile];

                        if (IsEligibleForDelete(fileName))
                        {
                            // This is a controlled file
                            if (!Options.AutoDelete)
                            {
                                // Warn the user
                                IVsUIShell uiShell = (IVsUIShell)_sccProvider.GetService(typeof(SVsUIShell));
                                Guid clsid = Guid.Empty;
                                int dialogResult;
                                string messageText = Resources.TPD_DeleteControlledFile;
                                string messageCaption = Resources.ProviderName;
                                int result = uiShell.ShowMessageBox(
                                    0,
                                    ref clsid,
                                    messageCaption,
                                    String.Format(CultureInfo.CurrentUICulture, messageText, fileName),
                                    string.Empty,
                                    0,
                                    OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST,
                                    OLEMSGICON.OLEMSGICON_QUERY,
                                    0,
                                    // false = application modal; true would make it system modal
                                    out dialogResult);
                                if (result != VSConstants.S_OK || dialogResult != (int)DialogResult.Yes)
                                {
                                    pSummaryResult[0] = VSQUERYREMOVEFILERESULTS.VSQUERYREMOVEFILERESULTS_RemoveNotOK;
                                    if (rgResults != null)
                                    {
                                        rgResults[iFile] = VSQUERYREMOVEFILERESULTS.VSQUERYREMOVEFILERESULTS_RemoveNotOK;
                                    }
                                    // Don't spend time iterating through the rest of the files once the delete has been cancelled
                                    break;
                                }
                            }

                            // User has said okay, we're going to delete this controlled file.
                            vsSelectionFilesDeleted.FileNames.Add(fileName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(String.Format("SccProviderService.OnQueryRemove() Exception: {0}", ex.Message));
                pSummaryResult[0] = VSQUERYREMOVEFILERESULTS.VSQUERYREMOVEFILERESULTS_RemoveNotOK;
                if (rgResults != null)
                {
                    for (int iFile = 0; iFile < cFiles; iFile++)
                    {
                        rgResults[iFile] = VSQUERYREMOVEFILERESULTS.VSQUERYREMOVEFILERESULTS_RemoveNotOK;
                    }
                }
            }
            
            return VSConstants.S_OK;
        }

        public int OnAfterRemoveFiles([InAttribute] int cProjects, [InAttribute] int cFiles, [InAttribute] IVsProject[] rgpProjects, [InAttribute] int[] rgFirstIndices, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSREMOVEFILEFLAGS[] rgFlags)
        {
            for (int i = 0; i < vsSelectionFilesDeleted.FileNames.Count; i++)
            {
                string fileName = vsSelectionFilesDeleted.FileNames[i];
                bool exists = File.Exists(fileName);
                if (exists)
                {
                    // This file wasn't actually deleted, it was just excluded from the project
                    vsSelectionFilesDeleted.FileNames.RemoveAt(i--);
                }
                
            }
            if (vsSelectionFilesDeleted.FileNames.Count > 0)
            {
                DeleteFiles(vsSelectionFilesDeleted);
            }

            return VSConstants.S_OK;
        }

        public int OnQueryRemoveDirectories([InAttribute] IVsProject pProject, [InAttribute] int cDirectories, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSQUERYREMOVEDIRECTORYFLAGS[] rgFlags, [OutAttribute] VSQUERYREMOVEDIRECTORYRESULTS[] pSummaryResult, [OutAttribute] VSQUERYREMOVEDIRECTORYRESULTS[] rgResults)
        {
            return VSConstants.E_NOTIMPL;
        }

        public int OnAfterRemoveDirectories([InAttribute] int cProjects, [InAttribute] int cDirectories, [InAttribute] IVsProject[] rgpProjects, [InAttribute] int[] rgFirstIndices, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSREMOVEDIRECTORYFLAGS[] rgFlags)
        {
            return VSConstants.E_NOTIMPL;
        }

        public int OnQueryRenameFiles([InAttribute] IVsProject pProject, [InAttribute] int cFiles, [InAttribute] string[] rgszMkOldNames, [InAttribute] string[] rgszMkNewNames, [InAttribute] VSQUERYRENAMEFILEFLAGS[] rgFlags, [OutAttribute] VSQUERYRENAMEFILERESULTS[] pSummaryResult, [OutAttribute] VSQUERYRENAMEFILERESULTS[] rgResults)
        {
            for (int i = 0; i < cFiles; i++)
            {
                string sourceName = rgszMkOldNames[i];
                FileState state = GetFileState(sourceName);
                if (state == FileState.NotInPerforce)
                {
                    if (rgResults != null)
                    {
                        rgResults[i] = VSQUERYRENAMEFILERESULTS.VSQUERYRENAMEFILERESULTS_RenameOK;
                        pSummaryResult[i] = VSQUERYRENAMEFILERESULTS.VSQUERYRENAMEFILERESULTS_RenameOK;
                    }
                }
                else 
                {
                    if (state == FileState.OpenForEdit)
                    {
                        if (rgResults != null)
                        {
                            rgResults[i] = VSQUERYRENAMEFILERESULTS.VSQUERYRENAMEFILERESULTS_RenameOK;
                        }
                        pSummaryResult[i] = VSQUERYRENAMEFILERESULTS.VSQUERYRENAMEFILERESULTS_RenameOK;
                    }
                    else
                    {
                        if (rgResults != null)
                        {
                            rgResults[i] = VSQUERYRENAMEFILERESULTS.VSQUERYRENAMEFILERESULTS_RenameNotOK;
                        }
                        pSummaryResult[i] = VSQUERYRENAMEFILERESULTS.VSQUERYRENAMEFILERESULTS_RenameNotOK;
                        string msg = String.Format(Resources.Cant_Rename_File_filename_Because_It_Must_Be_Checked_Out, sourceName);
                        MessageBox.Show(msg, Resources.Rename_File, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }

            return VSConstants.S_OK;
        }

        /// <summary>
        /// Implement OnAfterRenameFiles event to rename a file in the source control store when it gets renamed in the project
        /// Also, rename the store if the project itself is renamed
        /// </summary>
        public int OnAfterRenameFiles([InAttribute] int cProjects, [InAttribute] int cFiles, [InAttribute] IVsProject[] rgpProjects, [InAttribute] int[] rgFirstIndices, [InAttribute] string[] rgszMkOldNames, [InAttribute] string[] rgszMkNewNames, [InAttribute] VSRENAMEFILEFLAGS[] rgFlags)
        {
            VsSelection vsSelection = GetVsSelectionNoFileNamesAllNodes();
            VsSelection vsSelectionUncontrolled = GetVsSelectionNoFileNamesAllNodes();

            // Start by iterating through all projects calling this function
            for (int iProject = 0; iProject < cProjects; iProject++)
            {
                // Files in this project are in rgszMkOldNames, rgszMkNewNames arrays starting with iProjectFilesStart index and ending at iNextProjecFilesStart-1
                int iProjectFilesStart = rgFirstIndices[iProject];
                int iNextProjecFilesStart = cFiles;
                if (iProject < cProjects - 1)
                {
                    iNextProjecFilesStart = rgFirstIndices[iProject+1];
                }

                // Now that we know which files belong to this project, iterate the project files
                for (int iFile = iProjectFilesStart; iFile < iNextProjecFilesStart; iFile++)
                {
                    string sourceName = rgszMkOldNames[iFile];
                    string targetName = rgszMkNewNames[iFile];
                    string fileName = sourceName + "|" + targetName;
                    if (IsEligibleForRename(sourceName))
                    {
                        vsSelection.FileNames.Add(fileName); // we'll be asking P4Cache to update the targetName, which shows in Solution Explorer
                    }
                    else
                    {
                        vsSelectionUncontrolled.FileNames.Add(fileName); // we'll be asking P4Cache to update the targetName, which shows in Solution Explorer
                    }
                }
            }

            if (vsSelection.FileNames.Count > 0)
            {
                RenameFiles(vsSelection);
            }

            if (vsSelectionUncontrolled.FileNames.Count > 0 && _isUsingP4Cache)
            {
                // Update the file states of renamed files.
                _p4Cache.AddOrUpdateFiles(vsSelectionUncontrolled);
            }

            return VSConstants.S_OK;
        }

        public int OnQueryRenameDirectories([InAttribute] IVsProject pProject, [InAttribute] int cDirs, [InAttribute] string[] rgszMkOldNames, [InAttribute] string[] rgszMkNewNames, [InAttribute] VSQUERYRENAMEDIRECTORYFLAGS[] rgFlags, [OutAttribute] VSQUERYRENAMEDIRECTORYRESULTS[] pSummaryResult, [OutAttribute] VSQUERYRENAMEDIRECTORYRESULTS[] rgResults)
        {
            return VSConstants.E_NOTIMPL;
        }

        public int OnAfterRenameDirectories([InAttribute] int cProjects, [InAttribute] int cDirs, [InAttribute] IVsProject[] rgpProjects, [InAttribute] int[] rgFirstIndices, [InAttribute] string[] rgszMkOldNames, [InAttribute] string[] rgszMkNewNames, [InAttribute] VSRENAMEDIRECTORYFLAGS[] rgFlags)
        {
            return VSConstants.E_NOTIMPL;
        }

        public int OnAfterSccStatusChanged([InAttribute] int cProjects, [InAttribute] int cFiles, [InAttribute] IVsProject[] rgpProjects, [InAttribute] int[] rgFirstIndices, [InAttribute] string[] rgpszMkDocuments, [InAttribute] uint[] rgdwSccStatus)
        {
            return VSConstants.E_NOTIMPL;
        }

        #region Files and Project Management Functions

        /// <summary>
        /// Returns whether this source control provider is the active scc provider.
        /// </summary>
        public bool IsActive
        {
            get { return _isActive; }
            set { _isActive = value; }
        }


        /// <summary>
        /// Returns the FileState status of the specified file
        /// Throws on connect error.
        /// </summary>
        /// <param name="vsFileName">the file name.</param>
        /// <returns>the Perforce file state, or FileState.NotSet if error.</returns>
        public FileState GetFileState(string vsFileName)
        {
            if (String.IsNullOrEmpty(vsFileName))
            {
                return FileState.NotSet;
            }

            FileState state;
            if (_isUsingP4Cache)
            {
                state = _p4Cache[vsFileName];
                return state;
            }

            return GetFileStateWithoutCache(vsFileName);
        }

        private FileState GetFileStateWithoutCache(string vsFilename)
        {
            if (P4Service == null)
            {
                return FileState.NotSet;
            }

            FileState state;
            try
            {
                P4Service.Connect();
            }
            catch (ArgumentException)
            {
                return FileState.NotSet;
            }
            catch (P4API.Exceptions.PerforceInitializationError)
            {
                return FileState.NotSet;
            }
            finally
            {
                string message;
                state = P4Service.GetVsFileState(vsFilename, out message);
                P4Service.Disconnect();
            }

            return state;
        }

        /// <summary>
        /// Execute the commandMethod named commandName if conditionMethod is true, on all files in selection. Return false if the command fails.
        /// </summary>
        /// <param name="vsSelection">The selected nodes and files.</param>
        /// <param name="commandName">The name of the command.</param>
        /// <param name="conditionMethod">The condition that must be true before executing commandMethod.</param>
        /// <param name="commandMethod">The command to execute.</param>
        /// <returns>false if the command fails.</returns>
        public bool ExecuteCommand(VsSelection vsSelection, string commandName, Func<string, bool> conditionMethod, Func<string, bool> commandMethod)
        {
            if (vsSelection.FileNames.Count <= 0)
            {
                return true;
            }

            Log.Debug(String.Format("SccProviderService.{0}: Executing command for {1} files on {2} nodes.", commandName, vsSelection.FileNames.Count, vsSelection.Nodes.Count));

            bool success = true;
            try
            {
                P4Service.Connect();
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (P4API.Exceptions.PerforceInitializationError)
            {
                return false;
            }
            finally
            {
                foreach (string fileName in vsSelection.FileNames)
                {
                    string sourceName = ConvertPipedFileNameToSource(fileName);
                    if (conditionMethod(sourceName))
                    {
                        if (!commandMethod(fileName))
                        {
                            success = false;
                        }
                    }
                }

                if (_isUsingP4Cache)
                {
                    _p4Cache.AddOrUpdateFilesBackground(vsSelection); // All nodes and file names in the solution will be refreshed when P4Cache.P4CacheUpdated is thrown.
                }

                P4Service.Disconnect();
            }

            return success;
        }

        /// <summary>
        /// If fileName is piped, like sourceName|targetName, return the targetName
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        private static string ConvertPipedFileNameToSource(string fileName)
        {
            string[] splits = fileName.Split('|');
            return splits[0];
        }

        /// <summary>
        /// Checkout the specified files from source control
        /// </summary>
        /// <param name="selection">the selected file names and nodes.</param>
        /// <returns>false if the command fails.</returns>
        public bool CheckoutFiles(VsSelection selection)
        {
            return ExecuteCommand(selection, Resources.Checkout_Files, IsEligibleForCheckOut, CheckoutFile);
        }

        /// <summary>
        /// Checkout the specified file from source control.
        /// </summary>
        /// <param name="fileName">the file name.</param>
        /// <returns>false if the command fails.</returns>
        public bool CheckoutFile(string fileName)
        {
            string message;
            bool success = P4Service.EditFile(fileName, out message);
            return success;
        }

        /// <summary>
        /// Lock the specified files. Currently used only for unit testing
        /// </summary>
        /// <param name="selection">the selected file names and nodes.</param>
        /// <returns>false if the command fails.</returns>
        public bool LockFiles(VsSelection selection)
        {
            // Used only for unit testing, or we should write an IsEligibleForLock()
            return ExecuteCommand(selection, Resources.Lock_Files, IsEligibleForRevertIfUnchanged, LockFile);
        }

        /// <summary>
        /// Marks the specified file to be locked
        /// </summary>
        /// <param name="fileName">the file name.</param>
        /// <returns>false if the command fails.</returns>
        public bool LockFile(string fileName)
        {
            string message;
            bool success = P4Service.LockFile(fileName, out message);
            return success;
        }

        /// <summary>
        /// Add (actually mark for add) the specified files from source control
        /// </summary>
        /// <param name="selection">the selected file names and nodes.</param>
        /// <returns>false if the command fails.</returns>
        public bool AddFiles(VsSelection selection)
        {
            // First force current file states for new files into the cache
            if (_isUsingP4Cache)
            {
                _p4Cache.AddOrUpdateFiles(selection);
            }

            return ExecuteCommand(selection, Resources.Add_Files, IsEligibleForAdd, AddFile);
        }

        /// <summary>
        /// Marks the specified file to be added to Perforce
        /// </summary>
        /// <param name="fileName">the file name.</param>
        /// <returns>false if the command fails.</returns>
        public bool AddFile(string fileName)
        {
            string message;
            bool success = P4Service.AddFile(fileName, out message);
            return success;
        }

        /// <summary>
        /// Reverts the specified files if they are unchanged from the head revision
        /// </summary>
        /// <param name="selection">the selected file names and nodes.</param>
        /// <returns>false if the command fails.</returns>
        public bool RevertFilesIfUnchanged(VsSelection selection)
        {
            return ExecuteCommand(selection, Resources.Revert_Files_If_Unchanged, IsEligibleForRevertIfUnchanged, RevertFileIfUnchanged);
        }

        /// <summary>
        /// Reverts the specified file if it is unchanged from the head revision
        /// </summary>
        /// <param name="fileName">the file name.</param>
        /// <returns>false if the command fails.</returns>
        public bool RevertFileIfUnchanged(string fileName)
        {
            string message;
            bool success = P4Service.RevertIfUnchangedFile(fileName, out message);
            return success;
        }

        /// <summary>
        /// Reverts the specified files 
        /// </summary>
        /// <param name="selection">the selected file names and nodes.</param>
        /// <returns>false if the command fails.</returns>
        public bool RevertFiles(VsSelection selection)
        {
            if (Options.PromptBeforeRevert)
            {
                DialogResult result = MessageBox.Show(
                    Resources.revertPrompt,
                    Resources.Revert,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (result != DialogResult.Yes)
                {
                    return false;
                }
            }

            return ExecuteCommand(selection, Resources.Revert_Files, IsEligibleForRevert, RevertFile);
        }

        /// <summary>
        /// Reverts the specified file
        /// </summary>
        /// <param name="fileName">the file name.</param>
        /// <returns>false if the command fails.</returns>
        public bool RevertFile(string fileName)
        {
            string message;
            bool success = P4Service.RevertFile(fileName, out message);
            return success;
        }

        /// <summary>
        /// Removes (marks for delete) the specified files 
        /// </summary>
        /// <param name="selection">the selected file names and nodes.</param>
        /// <returns>false if the command fails.</returns>
        public bool DeleteFiles(VsSelection selection)
        {
            return ExecuteCommand(selection, Resources.Delete_Files, IsEligibleForDelete, DeleteFile);
        }

        /// <summary>
        /// Marks the specified file to be deleted from Perforce.
        /// Currently we don't have a command for this. It's done as a part of VS file removal, if AutoDelete is set.
        /// </summary>
        /// <param name="fileName">the file name.</param>
        /// <returns>false if the command fails.</returns>
        public bool DeleteFile(string fileName)
        {
            string message;
            if (IsEligibleForRevert(fileName))
            {
                // We must revert this file before we can mark it for delete
                if (Options.PromptBeforeRevert)
                {
                    DialogResult result = MessageBox.Show(
                        Resources.revertPrompt,
                        Resources.Revert,
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    if (result != DialogResult.Yes)
                    {
                        return false;
                    }
                } 
                P4Service.RevertFile(fileName, out message);
            }

            bool success = P4Service.DeleteFile(fileName, out message);
            return success;
        }

        /// <summary>
        /// Rename (actually move) the specified files.
        /// Note that for purposes of refactoring, fileNames are piped, as in sourceFile|targetFile.
        /// Files must be checked out before you can rename them.
        /// </summary>
        /// <param name="selection">the file names and nodes.</param>
        /// <returns>false if the command fails.</returns>
        public bool RenameFiles(VsSelection selection)
        {
            // First force current file states for new files into the cache
            if (_isUsingP4Cache)
            {
                _p4Cache.AddOrUpdateFiles(selection);
            }

            return ExecuteCommand(selection, Resources.Rename_Files, IsEligibleForRename, RenameFile);
        }

        /// <summary>
        /// Rename (actually move) the specified file.
        /// Note that for purposes of refactoring, fileNames are piped, as in sourceFile|targetFile.
        /// </summary>
        /// <param name="pipedFileName">the file name, piped, as in sourceFile|targetFile.</param>
        /// <returns>false if the command fails.</returns>
        public bool RenameFile(string pipedFileName)
        {
            string[] splits = pipedFileName.Split('|');
            bool success = false;
            if (splits.Length < 2)
            {
                string msg = String.Format(Resources.RenameFile_expects_piped_fileName, pipedFileName);
                Log.Error(msg);
                throw new ArgumentException(msg);
            }

            string sourceName = splits[0];
            string targetName = splits[1];

            if (File.Exists(targetName) && !File.Exists(sourceName))
            {
                // VS has already done the rename. Try to undo that so we can let Perforce do it again, below.
                try
                {
                    File.Copy(targetName, sourceName);
                    if (File.Exists(sourceName))
                    {
                        File.Delete(targetName);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(String.Format("SccProviderService.RenameFile() Exception: {0}", ex.Message));
                }
            }

            string message;
            try
            {
                P4Service.Connect();
            }
            catch (ArgumentException ex)
            {
                Log.Error(String.Format("SccProviderService.RenameFile() Exception: {0}", ex.Message));
            }
            catch (P4API.Exceptions.PerforceInitializationError ex)
            {
                Log.Error(String.Format("SccProviderService.RenameFile() Exception: {0}", ex.Message));
            }
            finally
            {
                success = P4Service.MoveFile(sourceName, targetName, out message);
                P4Service.Disconnect();
            }
            return success;
        }

        /// <summary>
        /// Refresh all glyphs in the solution
        /// </summary>
        public void Refresh()
        {
            VsSelection vsSelection = _sccProvider.GetSolutionSelection();
            _p4Cache.AddOrUpdateFilesBackground(vsSelection);

            // All nodes and fileNames in the selection will be refreshed when P4Cache.P4CacheUpdated is thrown.
        }

        /// <summary>
        /// Get Latest Revision for the specified files 
        /// </summary>
        /// <param name="selection">the selected file names and nodes.</param>
        /// <returns>false if the command fails.</returns>
        public bool GetLatestRevision(VsSelection selection)
        {
            return ExecuteCommand(selection, Resources.Get_Latest_Revision, IsEligibleForGetLatestRevision, GetLatestRevision);
        }

        /// <summary>
        /// Get the latest revision of file
        /// </summary>
        /// <param name="fileName">the file name.</param>
        /// <returns>false if the command fails.</returns>
        public bool GetLatestRevision(string fileName)
        {
            string message;
            bool success = P4Service.SyncFile(fileName, out message);
            return success;
        }

        /// <summary>
        /// View Revision History report for the specified files 
        /// </summary>
        /// <param name="selection">the selected file names and nodes.</param>
        /// <returns>false if the command fails.</returns>
        public bool RevisionHistory(VsSelection selection)
        {
            return ExecuteCommand(selection, Resources.Revision_History, IsEligibleForRevisionHistory, RevisionHistory);
        }

        /// <summary>
        /// Show the Revision History Report in P4V.exe
        /// </summary>
        /// <param name="fileName">the file name.</param>
        /// <returns>false if the command fails.</returns>
        public bool RevisionHistory(string fileName)
        {
            return P4Service.RevisionHistory(fileName);
        }


        /// <summary>
        /// View Diff report for the specified files 
        /// </summary>
        /// <param name="selection">the selected file names and nodes.</param>
        /// <returns>false if the command fails.</returns>
        public bool Diff(VsSelection selection)
        {
            return ExecuteCommand(selection, Resources.Diff, IsEligibleForDiff, Diff);
        }

        /// <summary>
        /// Show the Diff Report (Diff of head revision against workspace file) for fileName
        /// </summary>
        /// 
        /// <param name="fileName">the file name.</param>
        /// <returns>false if the command fails.</returns>
        public bool Diff(string fileName)
        {
            return P4Service.Diff(fileName);
        }

        /// <summary>
        /// View Time-Lapse report for the specified files 
        /// </summary>
        /// <param name="selection">the selected file names and nodes.</param>
        /// <returns>false if the command fails.</returns>
        public bool TimeLapse(VsSelection selection)
        {
            return ExecuteCommand(selection, Resources.Time_Lapse, IsEligibleForTimeLapse, TimeLapse);
        }

        /// <summary>
        /// Show the Time-Lapse Report for fileName
        /// </summary>
        /// <param name="fileName">the file name.</param>
        /// <returns>false if the command fails.</returns>
        public bool TimeLapse(string fileName)
        {
            return P4Service.TimeLapse(fileName);
        }

        #endregion

        /// <summary>
        /// Load options that have either been persisted in previous sessions, or saved in the current session via SaveOptions.
        /// </summary>
        /// <returns>P4Options object containing the current options saved by the user.</returns>
        internal P4Options LoadOptions()
        {
            return P4Options.Load(dte2);
        }

        /// <summary>
        /// Save options locally and persisted between sessions.
        /// </summary>
        /// <param name="options">The options to save.</param>
        internal void SaveOptions(P4Options options)
        {
            Options = options;
            options.Save(dte2);
            Log.OptionsLevel = options.LogLevel;

            if (_isActive && IsSolutionLoaded)
            {
                StartP4ServiceAndInitializeCache();
            }
        }

        #region IVsSccGlyphs Members

        // Remember the base index where our custom scc glyph start
        private uint _customSccGlyphBaseIndex;

        // Our custom image list
        ImageList _customSccGlyphsImageList;


        public int GetCustomGlyphList(uint BaseIndex, out uint pdwImageListHandle)
        {
            // If this is the first time we got called, construct the image list, remember the index, etc
            if (_customSccGlyphsImageList == null)
            {
                // The shell calls this function when the provider becomes active to get our custom glyphs
                // and to tell us what's the first index we can use for our glyphs
                // Remember the index in the scc glyphs (VsStateIcon) where our custom glyphs will start
                _customSccGlyphBaseIndex = BaseIndex;

                // Create a new imagelist
                _customSccGlyphsImageList = new ImageList();

                // Set the transparent color for the imagelist (the SccGlyphs.bmp uses magenta for background)
                _customSccGlyphsImageList.TransparentColor = Color.FromArgb(255, 0, 255);

                // Set the corret imagelist size (7x16 pixels, otherwise the system will either stretch the image or fill in with black blocks)
                _customSccGlyphsImageList.ImageSize = new Size(7, 16);

                // Add the custom scc glyphs we support to the list
                // NOTE: VS2005 and VS2008 are limited to 4 custom scc glyphs (let's hope this will change in future versions)
                var sccGlyphs = (Image)Resources.SccGlyphs4;
                _customSccGlyphsImageList.Images.AddStrip(sccGlyphs);
            }

            // Return a Win32 HIMAGELIST handle to our imagelist to the shell (by keeping the ImageList a member of the class we guarantee the Win32 object is still valid when the shell needs it)
            pdwImageListHandle = (uint)_customSccGlyphsImageList.Handle;


            // Return success (If you don't want to have custom glyphs return VSConstants.E_NOTIMPL)
            return VSConstants.S_OK;
        }

        #endregion IVsSccGlyphs Members

        /// <summary>
        /// Returns true if fileName is eligible to be checked out (opened for edit)
        /// </summary>
        /// <param name="fileName">the file name</param>
        /// <returns>true if fileName is eligible to be checked out (opened for edit)</returns>
        public bool IsEligibleForCheckOut(string fileName)
        {
            FileState state = GetFileState(fileName);
            switch (state)
            {
                case FileState.NotSet:
                case FileState.NotInPerforce:
                case FileState.OpenForEdit:
                case FileState.OpenForEditDiffers:
                case FileState.Locked: // Locked implies also Open For Edit
                case FileState.OpenForDelete:
                case FileState.DeletedAtHeadRevision:
                case FileState.OpenForAdd:
                case FileState.OpenForRenameSource:
                case FileState.OpenForRenameTarget:
                case FileState.NeedsResolved:
                case FileState.OpenForBranch:
                    return false;
                case FileState.OpenForDeleteOtherUser:
                case FileState.OpenForEditOtherUser:
                case FileState.LockedByOtherUser:
                case FileState.CheckedInHeadRevision:
                case FileState.CheckedInPreviousRevision:
                case FileState.OpenForIntegrate:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Returns true if fileName is eligible to be deleted (marked for delete)
        /// </summary>
        /// <param name="fileName">the file name</param>
        /// <returns>true if fileName is eligible to be deleted (marked for delete)</returns>
        public bool IsEligibleForDelete(string fileName)
        {
            FileState state = GetFileState(fileName);
            switch (state)
            {
                case FileState.NotSet:
                case FileState.NotInPerforce:
                case FileState.OpenForEdit:
                case FileState.OpenForEditDiffers:
                case FileState.Locked: // Locked implies also Open For Edit
                case FileState.OpenForDelete:
                case FileState.DeletedAtHeadRevision:
                case FileState.OpenForAdd:
                case FileState.OpenForRenameSource:
                case FileState.OpenForRenameTarget:
                case FileState.NeedsResolved:
                case FileState.OpenForBranch:
                case FileState.OpenForIntegrate:
                    return false;
                case FileState.OpenForDeleteOtherUser:
                case FileState.OpenForEditOtherUser:
                case FileState.LockedByOtherUser:
                case FileState.CheckedInHeadRevision:
                case FileState.CheckedInPreviousRevision:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Returns true if fileName is eligible to be renamed (moved)
        /// </summary>
        /// <param name="fileName">the file name</param>
        /// <returns>true if fileName is eligible to be renamed (moved)</returns>
        public bool IsEligibleForRename(string fileName)
        {
            FileState state = GetFileState(fileName);
            switch (state)
            {
                case FileState.NotSet:
                case FileState.NotInPerforce:
                case FileState.OpenForDelete:
                case FileState.DeletedAtHeadRevision:
                case FileState.OpenForAdd:
                case FileState.OpenForRenameSource:
                case FileState.NeedsResolved:
                case FileState.OpenForBranch:
                case FileState.OpenForIntegrate:
                case FileState.CheckedInHeadRevision:
                case FileState.CheckedInPreviousRevision:
                    return false;
                case FileState.OpenForEdit:
                case FileState.OpenForEditDiffers:
                case FileState.Locked: // Locked implies also Open For Edit
                case FileState.OpenForDeleteOtherUser:
                case FileState.OpenForEditOtherUser:
                case FileState.LockedByOtherUser:
                case FileState.OpenForRenameTarget:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Returns true if fileName is eligible to be added (marked for add)
        /// </summary>
        /// <param name="fileName">the file name</param>
        /// <returns>true if fileName is eligible to be added (marked for add)</returns>
        public bool IsEligibleForAdd(string fileName)
        {
            FileState state = GetFileState(fileName);
            switch (state)
            {
                case FileState.NotInPerforce:
                    return true;
                case FileState.NotSet:
                case FileState.OpenForEdit:
                case FileState.OpenForEditOtherUser:
                case FileState.OpenForEditDiffers:
                case FileState.Locked:
                case FileState.LockedByOtherUser:
                case FileState.OpenForDelete:
                case FileState.OpenForDeleteOtherUser:
                case FileState.DeletedAtHeadRevision:
                case FileState.OpenForAdd:
                case FileState.OpenForRenameSource:
                case FileState.OpenForRenameTarget:
                case FileState.CheckedInHeadRevision:
                case FileState.CheckedInPreviousRevision:
                case FileState.NeedsResolved:
                case FileState.OpenForBranch:
                case FileState.OpenForIntegrate:
                    return false;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Returns true if fileName is eligible to be reverted if it's unchanged
        /// </summary>
        /// <param name="fileName">the file name</param>
        /// <returns>true if fileName is eligible to be reverted if it's unchanged</returns>
        public bool IsEligibleForRevertIfUnchanged(string fileName)
        {
            FileState state = GetFileState(fileName);
            switch (state)
            {
                case FileState.NotSet:
                case FileState.NotInPerforce:
                case FileState.OpenForEditOtherUser:
                case FileState.LockedByOtherUser:
                case FileState.OpenForDelete:
                case FileState.OpenForDeleteOtherUser:
                case FileState.DeletedAtHeadRevision:
                case FileState.OpenForAdd:
                case FileState.OpenForRenameSource:
                case FileState.OpenForRenameTarget:
                case FileState.CheckedInHeadRevision:
                case FileState.CheckedInPreviousRevision:
                case FileState.OpenForIntegrate:
                case FileState.OpenForBranch:
                    return false;
                case FileState.OpenForEdit:
                case FileState.OpenForEditDiffers:
                case FileState.Locked: // implies also open for edit
                case FileState.NeedsResolved:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Returns true if fileName is eligible to be reverted
        /// </summary>
        /// <param name="fileName">the file name</param>
        /// <returns>true if fileName is eligible to be reverted</returns>
        public bool IsEligibleForRevert(string fileName)
        {
            FileState state = GetFileState(fileName);
            switch (state)
            {
                case FileState.NotSet:
                case FileState.NotInPerforce:
                case FileState.OpenForEditOtherUser:
                case FileState.LockedByOtherUser:
                case FileState.OpenForDeleteOtherUser:
                case FileState.DeletedAtHeadRevision:
                case FileState.CheckedInHeadRevision:
                case FileState.CheckedInPreviousRevision:
                case FileState.OpenForRenameSource:
                case FileState.OpenForRenameTarget: // P4 allows this, but we don't because it confuses VS, which doesn't see the rename
                    return false;
                case FileState.OpenForEdit:
                case FileState.OpenForEditDiffers:
                case FileState.Locked: // implies also open for edit
                case FileState.NeedsResolved:
                case FileState.OpenForBranch:
                case FileState.OpenForIntegrate:
                case FileState.OpenForDelete:
                case FileState.OpenForAdd:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Returns true if fileName has ever been submitted to Perforce and thus is eligible for one of of the report commands
        /// </summary>
        /// <param name="fileName">the file name</param>
        /// <returns>true if fileName has ever been submitted to Perforce and thus is eligible for one of of the report commands</returns>
        public bool IsEligibleForTimeLapse(string fileName)
        {
            return IsEligibleForRevisionHistory(fileName);
        }

        /// <summary>
        /// Returns true if fileName has ever been submitted to Perforce and thus is eligible for one of of the report commands
        /// </summary>
        /// <param name="fileName">the file name</param>
        /// <returns>true if fileName has ever been submitted to Perforce and thus is eligible for one of of the report commands</returns>
        public bool IsEligibleForRevisionHistory(string fileName)
        {
            FileState state = GetFileState(fileName);
            switch (state)
            {
                case FileState.NotSet:
                case FileState.NotInPerforce:
                case FileState.OpenForAdd:
                case FileState.OpenForRenameTarget: // is okay for diff
                case FileState.OpenForBranch:
                    return false;
                case FileState.OpenForEdit:
                case FileState.OpenForEditOtherUser:
                case FileState.OpenForEditDiffers:
                case FileState.Locked:
                case FileState.LockedByOtherUser:
                case FileState.OpenForDelete:
                case FileState.OpenForDeleteOtherUser:
                case FileState.DeletedAtHeadRevision:
                case FileState.OpenForRenameSource: // is okay for time-lapse and revision history, in P4V
                case FileState.CheckedInHeadRevision:
                case FileState.CheckedInPreviousRevision:
                case FileState.NeedsResolved:
                case FileState.OpenForIntegrate:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Returns true if fileName has ever been submitted to Perforce and thus is eligible for one of of the report commands
        /// </summary>
        /// <param name="fileName">the file name</param>
        /// <returns>true if fileName has ever been submitted to Perforce and thus is eligible for one of of the report commands</returns>
        public bool IsEligibleForDiff(string fileName)
        {
            FileState state = GetFileState(fileName);
            switch (state)
            {
                case FileState.NotSet:
                case FileState.NotInPerforce:
                case FileState.OpenForAdd:
                case FileState.OpenForRenameSource: // is okay for time-lapse and revision history, in P4V
                case FileState.DeletedAtHeadRevision:
                case FileState.OpenForBranch:
                    return false;
                case FileState.OpenForEdit:
                case FileState.OpenForEditOtherUser:
                case FileState.OpenForEditDiffers:
                case FileState.Locked:
                case FileState.LockedByOtherUser:
                case FileState.OpenForDelete:
                case FileState.OpenForDeleteOtherUser:
                case FileState.OpenForRenameTarget: // is okay for diff
                case FileState.CheckedInHeadRevision:
                case FileState.CheckedInPreviousRevision:
                case FileState.NeedsResolved:
                case FileState.OpenForIntegrate:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Return true if fileName is eligible for GetLatestRevision
        /// </summary>
        /// <param name="fileName">the file name</param>
        /// <returns>true if fileName is eligible for GetLatestRevision</returns>
        public bool IsEligibleForGetLatestRevision(string fileName)
        {
            FileState state = GetFileState(fileName);
            switch (state)
            {
                case FileState.NotSet:
                case FileState.NotInPerforce:
                case FileState.OpenForAdd:
                case FileState.CheckedInHeadRevision:
                case FileState.OpenForDelete:
                case FileState.OpenForBranch:
                case FileState.OpenForIntegrate:
                    return false;
                case FileState.OpenForEdit:
                case FileState.OpenForEditOtherUser:
                case FileState.OpenForEditDiffers:
                case FileState.Locked:
                case FileState.LockedByOtherUser:
                case FileState.OpenForDeleteOtherUser:
                case FileState.DeletedAtHeadRevision:
                case FileState.OpenForRenameSource:
                case FileState.OpenForRenameTarget:
                case FileState.CheckedInPreviousRevision:
                case FileState.NeedsResolved:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}