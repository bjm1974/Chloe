﻿using Chloe.Core;
using Chloe.Core.Visitors;
using Chloe.DbExpressions;
using Chloe.Descriptors;
using Chloe.Entity;
using Chloe.Exceptions;
using Chloe.Infrastructure;
using Chloe.InternalExtensions;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Chloe.SqlServer
{
    public class MsSqlContext : DbContext
    {
        DbContextServiceProvider _dbContextServiceProvider;
        public MsSqlContext(string connString)
            : this(new DefaultDbConnectionFactory(connString))
        {
        }

        public MsSqlContext(IDbConnectionFactory dbConnectionFactory)
        {
            Utils.CheckNull(dbConnectionFactory);

            this.PagingMode = PagingMode.ROW_NUMBER;
            this._dbContextServiceProvider = new DbContextServiceProvider(dbConnectionFactory, this);
        }

        static Dictionary<string, SysType> SysTypes;
        static MsSqlContext()
        {
            List<SysType> sysTypes = new List<SysType>();
            sysTypes.Add(new SysType<Byte[]>("image"));
            sysTypes.Add(new SysType<string>("text"));
            sysTypes.Add(new SysType<Guid>("uniqueidentifier"));
            sysTypes.Add(new SysType<DateTime>("date"));
            sysTypes.Add(new SysType<TimeSpan>("time"));
            sysTypes.Add(new SysType<DateTime>("datetime2"));
            //sysTypes.Add(new SysType<string>("datetimeoffset"));
            sysTypes.Add(new SysType<byte>("tinyint"));
            sysTypes.Add(new SysType<Int16>("smallint"));
            sysTypes.Add(new SysType<int>("int"));
            sysTypes.Add(new SysType<DateTime>("smalldatetime"));
            sysTypes.Add(new SysType<float>("real"));
            sysTypes.Add(new SysType<decimal>("money"));
            sysTypes.Add(new SysType<DateTime>("datetime"));
            sysTypes.Add(new SysType<double>("float"));
            //sysTypes.Add(new SysType<string>("sql_variant"));
            sysTypes.Add(new SysType<string>("ntext"));
            sysTypes.Add(new SysType<bool>("bit"));
            sysTypes.Add(new SysType<decimal>("decimal"));
            sysTypes.Add(new SysType<decimal>("numeric"));
            sysTypes.Add(new SysType<decimal>("smallmoney"));
            sysTypes.Add(new SysType<long>("bigint"));
            //sysTypes.Add(new SysType<string>("hierarchyid"));
            //sysTypes.Add(new SysType<string>("geometry"));
            //sysTypes.Add(new SysType<string>("geography"));
            sysTypes.Add(new SysType<Byte[]>("varbinary"));
            sysTypes.Add(new SysType<string>("varchar"));
            sysTypes.Add(new SysType<Byte[]>("binary"));
            sysTypes.Add(new SysType<string>("char"));
            sysTypes.Add(new SysType<Byte[]>("timestamp"));
            sysTypes.Add(new SysType<string>("nvarchar"));
            sysTypes.Add(new SysType<string>("nchar"));
            sysTypes.Add(new SysType<string>("xml"));
            sysTypes.Add(new SysType<string>("sysname"));

            SysTypes = sysTypes.ToDictionary(a => a.TypeName, a => a);
        }

        /// <summary>
        /// 分页模式。
        /// </summary>
        public PagingMode PagingMode { get; set; }
        public override IDbContextServiceProvider DbContextServiceProvider
        {
            get { return this._dbContextServiceProvider; }
        }

        /// <summary>
        /// 利用 SqlBulkCopy 批量插入数据。
        /// </summary>
        /// <typeparam name="TEntity"></typeparam>
        /// <param name="entities"></param>
        /// <param name="batchSize">设置 SqlBulkCopy.BatchSize 的值</param>
        /// <param name="bulkCopyTimeout">设置 SqlBulkCopy.BulkCopyTimeout 的值</param>
        /// <param name="keepIdentity">是否保留源标识值。false 由数据库分配标识值。</param>
        public virtual void BulkInsert<TEntity>(List<TEntity> entities, int? batchSize = null, int? bulkCopyTimeout = null, bool keepIdentity = false)
        {
            Utils.CheckNull(entities);

            TypeDescriptor typeDescriptor = TypeDescriptor.GetDescriptor(typeof(TEntity));

            DataTable dtToWrite = ToSqlBulkCopyDataTable(entities, typeDescriptor);

            SqlBulkCopy sbc = null;

            bool shouldCloseConnection = false;
            SqlConnection conn = this.Session.CurrentConnection as SqlConnection;
            try
            {
                SqlTransaction externalTransaction = null;
                if (this.Session.IsInTransaction)
                {
                    externalTransaction = this.Session.CurrentTransaction as SqlTransaction;
                }

                SqlBulkCopyOptions sqlBulkCopyOptions = keepIdentity ? (SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.KeepIdentity) : SqlBulkCopyOptions.KeepNulls;
                sbc = new SqlBulkCopy(conn, sqlBulkCopyOptions, externalTransaction);

                using (sbc)
                {
                    if (batchSize != null)
                        sbc.BatchSize = batchSize.Value;

                    if ((string.IsNullOrEmpty(typeDescriptor.Table.Schema)))
                        sbc.DestinationTableName = typeDescriptor.Table.Name;
                    else
                        sbc.DestinationTableName = string.Format("[{0}].[{1}]", typeDescriptor.Table.Schema, typeDescriptor.Table.Name);

                    if (bulkCopyTimeout != null)
                        sbc.BulkCopyTimeout = bulkCopyTimeout.Value;

                    if (conn.State != ConnectionState.Open)
                    {
                        shouldCloseConnection = true;
                        conn.Open();
                    }

                    sbc.WriteToServer(dtToWrite);
                }
            }
            finally
            {
                if (conn != null)
                {
                    if (shouldCloseConnection && conn.State == ConnectionState.Open)
                        conn.Close();
                }
            }
        }


        DataTable ToSqlBulkCopyDataTable<TModel>(List<TModel> modelList, TypeDescriptor typeDescriptor)
        {
            DataTable dt = new DataTable();

            List<SysColumn> columns = GetTableColumns(typeDescriptor.Table.Name);
            List<ColumnMapping> columnMappings = new List<ColumnMapping>();

            var mappingMemberDescriptors = typeDescriptor.MappingMemberDescriptors.Select(a => a.Value).ToList();
            for (int i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                MappingMemberDescriptor mappingMemberDescriptor = mappingMemberDescriptors.Where(a => a.Column.Name == column.Name).FirstOrDefault();
                ColumnMapping columnMapping = new ColumnMapping(column);
                Type dataType;
                if (mappingMemberDescriptor == null)
                {
                    /*
                     * 由于 SqlBulkCopy 要求传入的列必须与表列一一对应，因此，如果 model 中没有与列对应的属性，则使用列数据类型的默认值
                     */

                    SysType sysType = GetSysTypeByTypeName(column.TypeName);
                    columnMapping.DefaultValue = column.IsNullable ? null : sysType.DetaultValue;
                    dataType = sysType.CSharpType;
                }
                else
                {
                    columnMapping.MapMember = mappingMemberDescriptor.MemberInfo;
                    dataType = mappingMemberDescriptor.MemberInfoType.GetUnderlyingType();
                    if (dataType.IsEnum)
                        dataType = typeof(int);
                }

                columnMappings.Add(columnMapping);
                dt.Columns.Add(new DataColumn(column.Name, dataType));
            }

            foreach (var model in modelList)
            {
                DataRow dr = dt.NewRow();
                for (int i = 0; i < columnMappings.Count; i++)
                {
                    ColumnMapping columnMapping = columnMappings[i];
                    MemberInfo member = columnMapping.MapMember;
                    object value = null;
                    if (member == null)
                    {
                        value = columnMapping.DefaultValue;
                    }
                    else
                    {
                        value = member.GetMemberValue(model);
                        if (member.GetMemberType().GetUnderlyingType().IsEnum)
                        {
                            if (value != null)
                                value = (int)value;
                        }
                    }

                    dr[i] = value ?? DBNull.Value;
                }

                dt.Rows.Add(dr);
            }

            return dt;
        }
        List<SysColumn> GetTableColumns(string tableName)
        {
            string sql = "select syscolumns.name,syscolumns.colorder,syscolumns.isnullable,systypes.xusertype,systypes.name as typename from syscolumns inner join systypes on syscolumns.xusertype=systypes.xusertype inner join sysobjects on syscolumns.id = sysobjects.id where sysobjects.xtype = 'U' and sysobjects.name = @TableName order by syscolumns.colid asc";

            List<SysColumn> columns = new List<SysColumn>();

            using (var reader = this.Session.ExecuteReader(sql, new DbParam("@TableName", tableName)))
            {
                while (reader.Read())
                {
                    SysColumn column = new SysColumn();
                    column.Name = GetValue<string>(reader, "name");
                    column.ColOrder = GetValue<int>(reader, "colorder");
                    column.XUserType = GetValue<int>(reader, "xusertype");
                    column.TypeName = GetValue<string>(reader, "typename");
                    column.IsNullable = GetValue<bool>(reader, "isnullable");

                    columns.Add(column);
                }

                reader.Close();
            }

            return columns;
        }

        static SysType GetSysTypeByTypeName(string typeName)
        {
            SysType sysType;
            if (SysTypes.TryGetValue(typeName, out sysType))
            {
                return sysType;
            }

            throw new NotSupportedException(string.Format("Does not Support systype '{0}'", typeName));
        }
        static T GetValue<T>(IDataReader reader, string name)
        {
            object val = reader.GetValue(reader.GetOrdinal(name));
            if (val == DBNull.Value)
            {
                val = null;
                return (T)val;
            }

            return (T)Convert.ChangeType(val, typeof(T).GetUnderlyingType());
        }


        class SysType<TCSharpType> : SysType
        {
            public SysType(string typeName)
            {
                this.TypeName = typeName;
                this.CSharpType = typeof(TCSharpType);
                this.DetaultValue = default(TCSharpType);
            }
        }
        class SysType
        {
            public string TypeName { get; set; }
            public Type CSharpType { get; set; }
            public object DetaultValue { get; set; }
        }
        class SysColumn
        {
            public string Name { get; set; }
            public int ColOrder { get; set; }
            public int XUserType { get; set; }
            public string TypeName { get; set; }
            public bool IsNullable { get; set; }
            public override string ToString()
            {
                return this.Name;
            }
        }
        class ColumnMapping
        {
            public ColumnMapping(SysColumn column)
            {
                this.Column = column;
            }
            public SysColumn Column { get; set; }
            public MemberInfo MapMember { get; set; }
            public object DefaultValue { get; set; }
        }
    }
}
