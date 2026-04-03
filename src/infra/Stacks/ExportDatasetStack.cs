using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class ExportDatasetStackProps : StackProps
{
    public required Repository ExportDatasetEcrRepo { get; init; }
    public required string ImageTag { get; init; }
    public required Bucket DataBucket { get; init; }
    public required Table LabelsTable { get; init; }
    public required Table ExportsTable { get; init; }
}

public class ExportDatasetStack : Stack
{
    public ExportDatasetStack(Construct scope, string id, ExportDatasetStackProps props) : base(scope, id, props)
    {
        var exportFunction = new DockerImageFunction(this, "ExportDatasetFunction", new DockerImageFunctionProps
        {
            FunctionName = "snout-spotter-export-dataset",
            Description = "Exports labelled training data as zip with ImageFolder structure",
            Code = DockerImageCode.FromEcr(props.ExportDatasetEcrRepo, new EcrImageCodeProps
            {
                TagOrDigest = props.ImageTag
            }),
            MemorySize = 2048,
            Timeout = Duration.Minutes(15),
            EphemeralStorageSize = Size.Mebibytes(5120),
            Environment = new Dictionary<string, string>
            {
                ["BUCKET_NAME"] = props.DataBucket.BucketName,
                ["LABELS_TABLE"] = props.LabelsTable.TableName,
                ["EXPORTS_TABLE"] = props.ExportsTable.TableName
            }
        });

        props.DataBucket.GrantRead(exportFunction);
        props.DataBucket.GrantPut(exportFunction, "training-exports/*");
        props.LabelsTable.GrantReadData(exportFunction);
        props.ExportsTable.GrantReadWriteData(exportFunction);

        _ = new CfnOutput(this, "ExportDatasetFunctionArn", new CfnOutputProps
        {
            Value = exportFunction.FunctionArn,
            Description = "ARN of the ExportDataset Lambda function"
        });
    }
}
