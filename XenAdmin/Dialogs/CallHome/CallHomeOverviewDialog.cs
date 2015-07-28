﻿/* Copyright (c) Citrix Systems Inc. 
 * All rights reserved. 
 * 
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met: 
 * 
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer. 
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using XenAdmin.Actions;
using XenAdmin.Core;
using XenAdmin.Dialogs.VMProtectionRecovery;
using XenAdmin.Network;
using XenAPI;
using XenCenterLib;


namespace XenAdmin.Dialogs.CallHome
{
    public partial class CallHomeOverviewDialog : XenDialogBase
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public CallHomeOverviewDialog()
        {
            InitializeComponent();
        }

        private Pool currentSelected = null;

        void worker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                log.Error(e.Error);
                return;
            }

            Program.Invoke(this, () => RefreshGrid((List<DataGridViewRow>)e.Result));
        }

        private void RefreshGrid(List<DataGridViewRow> rows)
        {
            Program.AssertOnEventThread();

            var selectedPool = currentSelected;
            poolsDataGridView.SuspendLayout();
            try
            {
                poolsDataGridView.Rows.Clear();

                foreach (var row in rows)
                {
                    if (poolsDataGridView.ColumnCount > 0)
                    {
                        poolsDataGridView.Rows.Add(row);
                    }
                }
                RefreshButtons();
                RefreshDetailsPanel();
                if (selectedPool != null)
                {
                    foreach (DataGridViewRow row in poolsDataGridView.Rows)
                    {
                        if (row is PoolRow &&
                            (row as PoolRow).Pool.uuid == selectedPool.uuid)
                        {
                            poolsDataGridView.ClearSelection();
                            row.Selected = true;
                            break;
                        }
                    }
                }
            }
            finally
            {
                poolsDataGridView.ResumeLayout();
            }
        }

        object worker_DoWork(object sender, object argument)
        {
            var list = new List<DataGridViewRow>();
            foreach (IXenConnection xenConnection in ConnectionsManager.XenConnectionsCopy)
            {
                if (!xenConnection.IsConnected || Helpers.FeatureForbidden(xenConnection, Host.RestrictHealthCheck)) 
                    continue;
                var pool = Helpers.GetPoolOfOne(xenConnection);
                if (pool != null)
                {
                    var poolRow = new PoolRow(pool);
                    list.Add(poolRow);
                }
            }
            return list;
        }

        void Pool_BatchCollectionChanged(object sender, EventArgs e)
        {
            LoadPools();
        }


        QueuedBackgroundWorker worker = new QueuedBackgroundWorker();
        private void LoadPools()
        {
            worker.RunWorkerAsync(worker_DoWork, worker_RunWorkerCompleted);
        }

        #region PoolRow
        private class PoolRow : DataGridViewRow
        {
            private DataGridViewTextAndImageCell _nameCell = new DataGridViewTextAndImageCell();
            private DataGridViewTextAndImageCell _statusCell = new DataGridViewTextAndImageCell();
            public readonly Pool Pool;
            public PoolRow(Pool pool)
            {
                Cells.Add(_nameCell);
                Cells.Add(_statusCell);
                Pool = pool;
                RefreshRow();
            }

            public void RefreshRow()
            {
                _nameCell.Value = Pool.Name;
                _nameCell.Image = null;
                _statusCell.Value = Pool.CallHomeSettings.StatusDescription;
                _statusCell.Image = Pool.CallHomeSettings.Status != CallHomeStatus.Enabled
                    ? Properties.Resources._000_error_h32bit_16
                    : Properties.Resources._000_Alert2_h32bit_16;
            }
        }
        #endregion
        
        
        private void RefreshButtons()
        {
            if (poolsDataGridView.SelectedRows.Count == 1 && poolsDataGridView.SelectedRows[0] is PoolRow)
            {
                currentSelected = (Pool)((PoolRow)poolsDataGridView.SelectedRows[0]).Pool;
            }
            else
            {
                if (poolsDataGridView.SelectedRows.Count == 0)
                    currentSelected = null;
            }

            poolDetailsPanel.Visible = (currentSelected != null);
        }

        private void RefreshDetailsPanel()
        {
            if (poolsDataGridView.SelectedRows.Count != 1 || !(poolsDataGridView.SelectedRows[0] is PoolRow))
            {
                poolNameLabel.Text = "";
                return;
            }

            var poolRow = (PoolRow)poolsDataGridView.SelectedRows[0];
            poolNameLabel.Text = poolRow.Pool.Name.Ellipsise(120);
            scheduleLabel.Text = GetScheduleDescription(poolRow.Pool.CallHomeSettings);
            lastUploadLabel.Visible = lastUploadDateLabel.Visible = !string.IsNullOrEmpty(poolRow.Pool.CallHomeSettings.LastSuccessfulUpload);
            lastUploadDateLabel.Text = GetLastUploadDescription(poolRow.Pool.CallHomeSettings);

            healthCheckStatusPanel.Visible = poolRow.Pool.CallHomeSettings.Status == CallHomeStatus.Enabled;
            notEnrolledPanel.Visible = poolRow.Pool.CallHomeSettings.Status != CallHomeStatus.Enabled;
            UpdateUploadRequestDescription(poolRow.Pool.CallHomeSettings);
        }

        public string GetScheduleDescription(CallHomeSettings callHomeSettings)
        {
            {
                var time = new DateTime(1900, 1, 1, callHomeSettings.TimeOfDay, 0, 0);
                return callHomeSettings.Status == CallHomeStatus.Enabled
                    ? string.Format(Messages.CALLHOME_SCHEDULE_DESCRIPTION, callHomeSettings.IntervalInWeeks,
                                    callHomeSettings.DayOfWeek, HelpersGUI.DateTimeToString(time, Messages.DATEFORMAT_HM, true))
                    : string.Empty;
            }
        }

        public void UpdateUploadRequestDescription(CallHomeSettings callHomeSettings)
        {
            {
                if (!callHomeSettings.CanRequestNewUpload)
                {
                        uploadRequestLinkLabel.Text = string.Format(Messages.HEALTHCHECK_ON_DEMAND_REQUESTED_AT,
                                                                    HelpersGUI.DateTimeToString(callHomeSettings.NewUploadRequestTime.ToLocalTime(), 
                                                                        Messages.DATEFORMAT_HM, true));
                    uploadRequestLinkLabel.LinkArea = new LinkArea(0, 0);
                    return;
                }
                uploadRequestLinkLabel.Text = Messages.HEALTHCHECK_ON_DEMAND_REQUEST;
                uploadRequestLinkLabel.LinkArea = new LinkArea(0, uploadRequestLinkLabel.Text.Length);
            }
        }

        public string GetLastUploadDescription(CallHomeSettings callHomeSettings)
        {
            if (!string.IsNullOrEmpty(callHomeSettings.LastSuccessfulUpload))
            {
                DateTime lastSuccessfulUpload;
                if (CallHomeSettings.TryParseStringToDateTime(callHomeSettings.LastSuccessfulUpload, out lastSuccessfulUpload))
                {
                    return HelpersGUI.DateTimeToString(lastSuccessfulUpload.ToLocalTime(), Messages.DATEFORMAT_DMY_HM, true);
                }
            }
            return string.Empty;
        }

        private void CallHomeOverview_Load(object sender, EventArgs e)
        {
            LoadPools();
            foreach (IXenConnection xenConnection in ConnectionsManager.XenConnectionsCopy)
            {
                xenConnection.Cache.RegisterBatchCollectionChanged<Pool>(Pool_BatchCollectionChanged);
            }
            showAgainCheckBox.Checked = Properties.Settings.Default.ShowHealthCheckEnrollmentReminder;
        }

        private void dataGridView1_SelectionChanged(object sender, EventArgs e)
        {
            RefreshButtons();
            RefreshDetailsPanel();
        }

        private void CallHomeOverviewDialog_FormClosed(object sender, FormClosedEventArgs e)
        {
            foreach (IXenConnection xenConnection in ConnectionsManager.XenConnectionsCopy)
            {
                xenConnection.Cache.DeregisterBatchCollectionChanged<Pool>(Pool_BatchCollectionChanged);
            }
        }

        private void buttonCancel_Click(object sender, EventArgs e)
        {
            Close();
        }
        
        private void editlinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (poolsDataGridView.SelectedRows.Count != 1 || !(poolsDataGridView.SelectedRows[0] is PoolRow))
                return;

            var poolRow = (PoolRow)poolsDataGridView.SelectedRows[0];
            new CallHomeSettingsDialog(poolRow.Pool, false).ShowDialog(this);
        }

        public DialogResult ShowDialog(IWin32Window parent, List<IXenObject> selectedItems)
        {
            SelectPool(selectedItems);
            return ShowDialog(parent);
        }

        public void RefreshView(List<IXenObject> selectedItems)
        {
            SelectPool(selectedItems);
            LoadPools();
        }

        private void SelectPool(List<IXenObject> selectedItems)
        {
            IXenObject xo = selectedItems.Count > 0 ? selectedItems.FirstOrDefault() : null;
            if (xo is Pool)
                currentSelected = xo as Pool;
        }

        private void enrollNowLinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (poolsDataGridView.SelectedRows.Count != 1 || !(poolsDataGridView.SelectedRows[0] is PoolRow))
                return;

            var poolRow = (PoolRow)poolsDataGridView.SelectedRows[0];
            new CallHomeSettingsDialog(poolRow.Pool, true).ShowDialog(this);
        }

        private void uploadRequestLinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (poolsDataGridView.SelectedRows.Count != 1 || !(poolsDataGridView.SelectedRows[0] is PoolRow))
                return;

            var poolRow = (PoolRow)poolsDataGridView.SelectedRows[0];
            var callHomeSettings = poolRow.Pool.CallHomeSettings;
            if (callHomeSettings.CanRequestNewUpload)
            {
                callHomeSettings.NewUploadRequest = CallHomeSettings.DateTimeToString(DateTime.UtcNow);
                var token = callHomeSettings.GetSecretyInfo(poolRow.Pool.Connection, CallHomeSettings.UPLOAD_TOKEN_SECRET);
                var user = callHomeSettings.GetSecretyInfo(poolRow.Pool.Connection, CallHomeSettings.UPLOAD_CREDENTIAL_USER_SECRET);
                var password = callHomeSettings.GetSecretyInfo(poolRow.Pool.Connection, CallHomeSettings.UPLOAD_CREDENTIAL_PASSWORD_SECRET);
                new SaveCallHomeSettingsAction(poolRow.Pool, callHomeSettings, token, user, password, false).RunAsync();
            }
        }

        private void showAgainCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (Properties.Settings.Default.ShowHealthCheckEnrollmentReminder != showAgainCheckBox.Checked)
            {
                Properties.Settings.Default.ShowHealthCheckEnrollmentReminder = showAgainCheckBox.Checked;
                Settings.TrySaveSettings();
            }
        }

        private void disableLinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (poolsDataGridView.SelectedRows.Count != 1 || !(poolsDataGridView.SelectedRows[0] is PoolRow))
                return;

            var poolRow = (PoolRow)poolsDataGridView.SelectedRows[0];
            if (poolRow.Pool == null)
                return;
            var callHomeSettings = poolRow.Pool.CallHomeSettings;
            if (callHomeSettings.Status == CallHomeStatus.Enabled)
            {
                string msg = Helpers.GetPool(poolRow.Pool.Connection) == null 
                    ? Messages.CONFIRM_DISABLE_HEALTH_CHECK_SERVER 
                    : Messages.CONFIRM_DISABLE_HEALTH_CHECK_POOL;
                using (var dlg = new ThreeButtonDialog(new ThreeButtonDialog.Details(null, msg, Messages.XENCENTER), 
                    ThreeButtonDialog.ButtonYes, ThreeButtonDialog.ButtonNo))
                {
                    if (dlg.ShowDialog(this) != DialogResult.Yes)
                        return;
                }
                callHomeSettings.Status = CallHomeStatus.Disabled;
                new SaveCallHomeSettingsAction(poolRow.Pool, callHomeSettings, null, null, null, false).RunAsync();
                
            }
        }
    }
}