using BaseOrm;

namespace BrokerCore.Data;

/// <summary>
/// Broker 資料庫封裝 —— BaseDb 的薄包裝
/// 透過 BaseOrm 透明支援 SQLite / PostgreSQL / SQL Server / MySQL
/// </summary>
public class BrokerDb : IDisposable
{
    private readonly BaseDb _db;

    /// <summary>
    /// 全 DB 存取序列化閘（reentrant Monitor）。
    /// <para>
    /// 底層 <see cref="BaseDb"/> 在交易進行中以<b>單一共享連線</b>承載交易（<c>_transactionConnection</c>），
    /// 且其 <c>GetConnection()</c> 對<b>任一執行緒</b>只要 <c>_transaction != null</c> 就回傳該交易連線。
    /// SQLite 連線非執行緒安全：若<b>讀取</b>不經此閘，背景交易（Thread B）進行中、請求執行緒（Thread A）
    /// 的讀取會搶用同一條交易連線並發操作 → 連線損壞、交易失敗，甚至殘留交易狀態使後續一律拋
    /// "A transaction is already active."。故<b>讀寫與交易全部</b>經此閘序列化。
    /// </para>
    /// 同執行緒可重入：<see cref="InTransaction"/> 持閘期間，交易主體內的讀取（如 GetNextSequence）
    /// 於同一執行緒重入取得閘、並正確看見交易連線；跨執行緒讀取則阻塞至交易結束（不再搶連線）。
    /// </summary>
    private readonly object _writeGate = new();

    /// <summary>SQLite（開發/單節點）</summary>
    public BrokerDb(string connectionString)
    {
        _db = new BaseDb(connectionString);
    }

    /// <summary>指定資料庫類型（叢集化部署）</summary>
    public BrokerDb(BaseDb db)
    {
        _db = db;
    }

    /// <summary>內部 BaseDb 實例（供進階操作）</summary>
    public BaseDb Db => _db;

    // ── 轉發 BaseDb 核心方法 ──

    public void EnsureTable<T>()
    {
        lock (_writeGate)
        {
            _db.EnsureTable<T>();
        }
    }

    public T? Get<T>(object id) where T : class, new()
    {
        lock (_writeGate)
        {
            return _db.Get<T>(id);
        }
    }

    public List<T> GetAll<T>() where T : new()
    {
        lock (_writeGate)
        {
            return _db.GetAll<T>();
        }
    }

    public long Insert<T>(T entity) where T : class
    {
        lock (_writeGate)
        {
            return _db.Insert(entity);
        }
    }

    public int Update<T>(T entity) where T : class
    {
        lock (_writeGate)
        {
            return _db.Update(entity);
        }
    }

    public int Delete<T>(object id)
    {
        lock (_writeGate)
        {
            return _db.Delete<T>(id);
        }
    }

    // 讀取也經 _writeGate 序列化：避免讀取在背景交易進行中搶用共享交易連線（見 _writeGate 說明）。
    public List<T> Query<T>(string sql, object? parameters = null) where T : new()
    {
        lock (_writeGate)
        {
            return _db.Query<T>(sql, parameters);
        }
    }

    public T? QueryFirst<T>(string sql, object? parameters = null) where T : class, new()
    {
        lock (_writeGate)
        {
            return _db.QueryFirst<T>(sql, parameters);
        }
    }

    public T? Scalar<T>(string sql, object? parameters = null)
    {
        lock (_writeGate)
        {
            return _db.Scalar<T>(sql, parameters);
        }
    }

    public int Execute(string sql, object? parameters = null)
    {
        lock (_writeGate)
        {
            return _db.Execute(sql, parameters);
        }
    }

    public void BeginTransaction()
    {
        lock (_writeGate)
        {
            _db.BeginTransaction();
        }
    }
    public void Commit()
    {
        lock (_writeGate)
        {
            _db.Commit();
        }
    }
    public void Rollback()
    {
        lock (_writeGate)
        {
            _db.Rollback();
        }
    }

    public void InTransaction(Action action)
    {
        lock (_writeGate)
        {
            _db.InTransaction(action);
        }
    }

    public T InTransaction<T>(Func<T> action)
    {
        lock (_writeGate)
        {
            return _db.InTransaction(action);
        }
    }

    public void Dispose() => _db.Dispose();

    // ── 工廠方法 ──

    public static BrokerDb UseSqlite(string connectionString)
        => new(connectionString);

    public static BrokerDb UsePostgreSql(string connectionString)
        => new(BaseDb.UsePostgreSql(connectionString));

    public static BrokerDb UseSqlServer(string connectionString)
        => new(BaseDb.UseSqlServer(connectionString));
}
